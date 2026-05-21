using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.LocalInference;
using Sutando.Pipeline;
using Sutando.Pipeline.Stages;
using Sutando.Realtime;

namespace Sutando.Voice.Local;

/// <summary>
/// A MEAI <see cref="IRealtimeClientSession"/> that drives one voice connection through the local
/// <see cref="Pipeline"/> (STT → Chat → TTS) instead of a cloud realtime API.
/// </summary>
/// <remarks>
/// <para>
/// This is the load-bearing piece of the <c>sutando voice --local</c> transport. The voice WS
/// server is unchanged — it still creates a <c>VoiceSession</c> around an
/// <see cref="IRealtimeClient"/> from <c>IRealtimeTransportFactory</c>, and the
/// <c>VoiceSession</c> read-loop still translates <see cref="RealtimeServerMessage"/>s through
/// <c>MeaiToSutandoEventAdapter</c>. The only swap is <i>which</i> <see cref="IRealtimeClient"/>
/// the factory hands out. So the wire protocol the browser already speaks is preserved for free.
/// </para>
/// <para>
/// <b>Pipeline shape</b>:
/// <c>ChannelPipelineSource → VadStage → SpeechToTextStage → ChatStage → TextToSpeechStage → RealtimeEventSink</c>.
/// The session writes inbound browser frames into the source's channel; the sink writes the
/// resulting MEAI messages into the outbound channel this session exposes through
/// <see cref="GetStreamingResponseAsync"/>.
/// </para>
/// <para>
/// <b>Connection lifecycle</b> mirrors <c>GeminiLiveRealtimeClientSession</c>: the pipeline is
/// started lazily on the first <see cref="GetStreamingResponseAsync"/> enumeration or the first
/// <see cref="SendAsync"/>, whichever comes first. Once started, the session immediately emits a
/// <c>SutandoSessionStarted</c> message so the WS server moves to <c>Listening</c> and the
/// browser may start sending audio.
/// </para>
/// </remarks>
public sealed class LocalPipelineRealtimeClientSession : IRealtimeClientSession
{
    private readonly LocalPipelineOptions? _options;
    private readonly string? _configurationError;
    private readonly ILogger<LocalPipelineRealtimeClientSession> _logger;

    private readonly ChannelPipelineSource _source = new();

    // Outbound MEAI messages the VoiceSession read-loop drains. Unbounded + single-reader: the
    // pipeline's own bounded inter-stage links apply back-pressure; this final hop must never
    // block the sink (a blocked sink would stall the whole pipeline).
    private readonly Channel<RealtimeServerMessage> _outbound = Channel.CreateUnbounded<RealtimeServerMessage>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

    private Pipeline.Pipeline? _pipeline;
    private Task? _pipelineTask;
    private bool _started;
    private bool _disposed;

    /// <summary>Create a session over the supplied local-pipeline configuration.</summary>
    /// <param name="options">The four pluggable stage components plus tuning knobs.</param>
    /// <param name="meaiOptions">The MEAI session options (model / voice / instructions). Stored on <see cref="Options"/>.</param>
    /// <param name="logger">Optional logger.</param>
    public LocalPipelineRealtimeClientSession(
        LocalPipelineOptions options,
        RealtimeSessionOptions? meaiOptions = null,
        ILogger<LocalPipelineRealtimeClientSession>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        Options = meaiOptions;
        _logger = logger ?? NullLogger<LocalPipelineRealtimeClientSession>.Instance;
    }

    /// <summary>
    /// Create a session that does no work and surfaces a single configuration error to the
    /// browser. Used when the local-inference models / config could not be resolved at server
    /// boot — the session still completes the handshake (so the browser shows the error rather
    /// than a dead socket), then emits the error and closes.
    /// </summary>
    /// <param name="configurationError">A human-readable explanation of what is missing.</param>
    /// <param name="meaiOptions">The MEAI session options. Stored on <see cref="Options"/>.</param>
    /// <param name="logger">Optional logger.</param>
    internal LocalPipelineRealtimeClientSession(
        string configurationError,
        RealtimeSessionOptions? meaiOptions,
        ILogger<LocalPipelineRealtimeClientSession>? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationError);
        _configurationError = configurationError;
        Options = meaiOptions;
        _logger = logger ?? NullLogger<LocalPipelineRealtimeClientSession>.Instance;
    }

    /// <inheritdoc/>
    public RealtimeSessionOptions? Options { get; }

    /// <inheritdoc/>
    public async Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        if (_configurationError is not null)
        {
            // Error-mode session — there is no pipeline to feed. Drop inbound frames silently.
            return;
        }

        switch (message)
        {
            case InputAudioBufferAppendRealtimeClientMessage audio:
            {
                // Browser microphone PCM. Wrap it in an AudioInputFrame at the configured input
                // format and push it into the pipeline head — the VAD stage marks turn
                // boundaries, the STT stage buffers and transcribes.
                var pcm = audio.Content.Data;
                if (!pcm.IsEmpty)
                {
                    var frame = new AudioFrame
                    {
                        SampleRate = _options!.InputSampleRateHz,
                        Channels = 1,
                        Encoding = AudioEncoding.Pcm16Le,
                        Pcm = pcm,
                        CapturedAt = DateTimeOffset.UtcNow,
                    };
                    await _source.Writer.WriteAsync(new AudioInputFrame(frame), cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            case CreateConversationItemRealtimeClientMessage create:
            {
                // A typed user turn (the browser's `text` envelope). Inject it as a final
                // TextFrame so the ChatStage treats it as a complete turn — bypassing STT.
                foreach (var content in create.Item.Contents)
                {
                    if (content is TextContent { Text: { Length: > 0 } text })
                    {
                        await _source.Writer.WriteAsync(new TextFrame(text, IsFinal: true), cancellationToken).ConfigureAwait(false);
                    }
                }
                return;
            }

            default:
                // The local transport has no model-side tool dispatch in this slice, so a tool
                // result has nowhere to go. Log rather than throw — a NotSupportedException here
                // would tear down an otherwise-healthy voice session.
                _logger.LogDebug(
                    "Local voice transport ignoring unsupported client message type '{Type}'.",
                    message.GetType().FullName);
                return;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var message in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsAssignableFrom(GetType()))
        {
            return this;
        }
        return null;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Complete the source so the pipeline drains its in-flight turn and exits cleanly; then
        // cancel the lifetime token as a hard backstop for any stage that ignores end-of-stream.
        _source.Complete();
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);

        if (_pipelineTask is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Swallow — we're tearing down. A faulted pipeline is logged in RunPipelineAsync.
            }
        }

        _outbound.Writer.TryComplete();
        _startGate.Dispose();
        _lifetimeCts.Dispose();
    }

    // --- pipeline lifecycle ------------------------------------------------

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_started)
        {
            return;
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            // SutandoSessionStarted is what MeaiToSutandoEventAdapter maps onto RealtimeSetupComplete
            // — the WS server gates the browser's "you may speak now" state on it. Emit it before
            // the pipeline produces anything else so the handshake completes promptly.
            _outbound.Writer.TryWrite(new RealtimeServerMessage
            {
                Type = SutandoRealtimeMessageTypes.SessionStarted,
            });

            if (_configurationError is { } error)
            {
                // Misconfigured local stack (missing model files). Surface the error right after
                // the handshake and complete the stream — the browser shows the message and the
                // socket closes cleanly, exactly like the missing-API-key path for Gemini mode.
                _logger.LogWarning("Local voice transport unavailable: {Error}", error);
                _outbound.Writer.TryWrite(new ErrorRealtimeServerMessage
                {
                    Error = new ErrorContent(error),
                });
                _outbound.Writer.TryComplete();
                _started = true;
                return;
            }

            _pipeline = BuildPipeline();
            _pipelineTask = Task.Run(() => RunPipelineAsync(_pipeline, _lifetimeCts.Token), CancellationToken.None);
            _started = true;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>
    /// Compose the STT → Chat → TTS pipeline. Internal so tests can assert the stage count
    /// without standing up the whole session.
    /// </summary>
    /// <returns>The fully-wired, not-yet-run pipeline.</returns>
    internal Pipeline.Pipeline BuildPipeline()
    {
        var options = _options
            ?? throw new InvalidOperationException("BuildPipeline called on an error-mode session.");
        var instructions = options.SystemPrompt ?? Options?.Instructions;

        return Pipeline.Pipeline.Builder()
            .StartWith(_source)
            .Then(new VadStage(options.VadDetector, options.VadOptions))
            .Then(new SpeechToTextStage(options.SpeechToText, options.SpeechToTextOptions))
            .Then(new ChatStage(options.Chat, instructions, options.ChatOptions))
            .Then(new TextToSpeechStage(options.TextToSpeech, options.TextToSpeechOptions))
            .EndsWith(new RealtimeEventSink(_outbound.Writer))
            .Build();
    }

    private async Task RunPipelineAsync(Pipeline.Pipeline pipeline, CancellationToken ct)
    {
        try
        {
            await pipeline.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on session dispose / browser disconnect.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local voice pipeline faulted.");

            // Surface the fault to the browser so the harness can show it rather than hanging
            // on a silently-dead pipeline. ErrorRealtimeServerMessage maps to RealtimeTransportError.
            _outbound.Writer.TryWrite(new ErrorRealtimeServerMessage
            {
                Error = new ErrorContent(ex.Message),
            });
        }
        finally
        {
            // The pipeline has stopped — no more messages will be produced. Completing the
            // outbound channel unblocks the VoiceSession read-loop's await foreach.
            _outbound.Writer.TryComplete();
        }
    }
}
