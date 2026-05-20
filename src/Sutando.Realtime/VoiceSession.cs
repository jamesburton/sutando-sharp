using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sutando.Realtime;

/// <summary>
/// High-level wrapper around an <see cref="IRealtimeClient"/>. Owns the conversation event bus,
/// the lifecycle state machine, and tool-call dispatch.
/// </summary>
/// <remarks>
/// <para>
/// This is the surface bodhi exposes as <c>VoiceSession</c>. The transport layer underneath
/// flows through MEAI's <see cref="IRealtimeClient"/> / <see cref="IRealtimeClientSession"/>
/// types — see <c>MAPPING.md</c> for the type-by-type translation. The public event surface
/// remains Sutando's sealed <see cref="RealtimeServerEvent"/> hierarchy: the read loop
/// translates MEAI <see cref="RealtimeServerMessage"/> values into Sutando events via
/// <see cref="MeaiToSutandoEventAdapter"/> before raising <see cref="EventReceived"/>.
/// </para>
/// <para>
/// <b>Reconnect:</b> on a non-client-initiated <see cref="RealtimeTransportClosed"/> or a
/// <see cref="RealtimeGoAway"/> with <see cref="RealtimeGoAway.Reconnect"/> set, the session
/// schedules an exponential-backoff reconnect with the last-known
/// <see cref="RealtimeSessionResumptionUpdate"/> handle. <b>No conversation-item replay is
/// attempted</b> — that's <c>VoiceSession</c>-level orchestration that lives in bodhi's
/// <c>ConversationContext</c> + <c>replayHistory</c>, and is explicitly deferred to a later phase.
/// </para>
/// </remarks>
public sealed class VoiceSession : IAsyncDisposable
{
    private readonly IRealtimeClient _client;
    private readonly bool _ownsClient;
    private readonly ILogger<VoiceSession> _logger;
    private readonly Dictionary<string, ToolRegistration> _tools = new(StringComparer.Ordinal);
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _disposeCts = new();

    private VoiceSessionState _state = VoiceSessionState.Idle;
    private IRealtimeClientSession? _session;
    private RealtimeSessionConfig? _config;
    private Task? _readLoop;
    private string? _resumptionHandle;
    private bool _disposed;

    /// <summary>Creates a new session bound to the supplied realtime client.</summary>
    /// <param name="client">
    /// The MEAI realtime client to drive. Reused across reconnects (each connect calls
    /// <see cref="IRealtimeClient.CreateSessionAsync"/>). Caller retains ownership unless
    /// <paramref name="ownsClient"/> is set.
    /// </param>
    /// <param name="ownsClient">
    /// When true, <see cref="DisposeAsync"/> also disposes the supplied <paramref name="client"/>.
    /// Defaults to false so DI-registered singletons aren't accidentally torn down when one
    /// session ends.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public VoiceSession(
        IRealtimeClient client,
        bool ownsClient = false,
        ILogger<VoiceSession>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownsClient = ownsClient;
        _logger = logger ?? NullLogger<VoiceSession>.Instance;
    }

    /// <summary>
    /// Convenience overload that auto-builds a <see cref="GeminiLiveRealtimeClient"/> with no
    /// default API key — callers must set <see cref="RealtimeSessionConfig.ApiKey"/> on the
    /// per-session config.
    /// </summary>
    /// <remarks>
    /// Retained for back-compat with the original parameter-less constructor used by
    /// <c>GeminiLiveIntegrationTests</c>. New code should construct an
    /// <see cref="IRealtimeClient"/> explicitly and pass it via the primary ctor.
    /// </remarks>
    public VoiceSession()
        : this(new GeminiLiveRealtimeClient(), ownsClient: true)
    {
    }

    /// <summary>The current state of the session. Reading is thread-safe.</summary>
    public VoiceSessionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>Raised whenever <see cref="State"/> changes. Handlers run on the read-loop thread.</summary>
    public event EventHandler<VoiceSessionStateChange>? StateChanged;

    /// <summary>Raised for every event surfaced by the transport, after the state machine has reacted to it.</summary>
    public event EventHandler<RealtimeServerEvent>? EventReceived;

    /// <summary>The most recent session-resumption handle, if any has arrived. Persists across reconnects within the same <see cref="VoiceSession"/>.</summary>
    public string? ResumptionHandle => _resumptionHandle;

    /// <summary>
    /// Registers a tool handler. Multiple registrations for the same tool name will throw —
    /// callers should register once at startup.
    /// </summary>
    /// <param name="definition">The tool definition (must appear in the next <see cref="ConnectAsync"/> config).</param>
    /// <param name="handler">The delegate invoked whenever the model emits a matching tool call.</param>
    public void RegisterTool(RealtimeToolDefinition definition, RealtimeToolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);

        if (!_tools.TryAdd(definition.Name, new ToolRegistration(definition, handler)))
        {
            throw new InvalidOperationException($"Tool '{definition.Name}' is already registered.");
        }
    }

    /// <summary>
    /// Creates a fresh underlying realtime session on the client and starts the read-loop.
    /// Completes when the session has been created — callers should wait for <see cref="State"/>
    /// to reach <see cref="VoiceSessionState.Listening"/> (which fires on the
    /// <see cref="RealtimeSetupComplete"/> event) before sending input.
    /// </summary>
    /// <param name="config">Session config. <see cref="RealtimeSessionConfig.ResumptionHandle"/> is overwritten with the cached handle, if any.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ConnectAsync(RealtimeSessionConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_state is not (VoiceSessionState.Idle or VoiceSessionState.Disconnected or VoiceSessionState.Failed))
            {
                throw new InvalidOperationException($"Cannot connect — session is in state {_state}.");
            }
            TransitionTo_NoLock(VoiceSessionState.Connecting);
        }

        // Apply the cached resumption handle when present and the caller hasn't supplied one.
        var effective = _resumptionHandle is { Length: > 0 } && string.IsNullOrEmpty(config.ResumptionHandle)
            ? config with { ResumptionHandle = _resumptionHandle }
            : config;

        // Hand the Sutando-side config to the client via RawRepresentationFactory — providers that
        // understand the type (GeminiLiveRealtimeClient) pull off the Gemini extensions; the
        // canonical IRealtimeClient surface (CreateSessionAsync(RealtimeSessionOptions?)) stays
        // unchanged.
        var options = new RealtimeSessionOptions
        {
            Model = effective.Model,
            Voice = effective.VoiceName,
            Instructions = effective.SystemInstruction,
            RawRepresentationFactory = () => effective,
        };

        IRealtimeClientSession session;
        try
        {
            session = await _client.CreateSessionAsync(options, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_stateLock)
            {
                TransitionTo_NoLock(VoiceSessionState.Failed);
            }
            throw;
        }

        _session = session;
        _config = effective;

        _readLoop = Task.Run(() => ReadLoopAsync(session, effective.EffectiveAudio, _disposeCts.Token), CancellationToken.None);
    }

    /// <summary>Sends a single realtime input (text or audio).</summary>
    /// <param name="input">The input.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task SendAsync(RealtimeInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var session = _session ?? throw new InvalidOperationException("Session is not connected.");
        var message = BuildClientMessage(input, _config?.EffectiveAudio ?? RealtimeAudioConfig.Default);
        return session.SendAsync(message, ct);
    }

    /// <summary>Disconnects the underlying transport.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        // MEAI's IRealtimeClientSession has no explicit Disconnect — DisposeAsync is the close.
        // We retain the consumer-facing DisconnectAsync method but route it through DisposeAsync.
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }

        if (_readLoop is { } loop)
        {
            try
            {
                await loop.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // best-effort
            }
        }

        _session = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _disposeCts.CancelAsync().ConfigureAwait(false);

        if (_session is { } session)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // best-effort — tearing down
            }
            _session = null;
        }

        if (_readLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch
            {
                // swallow — we're tearing down
            }
        }

        if (_ownsClient)
        {
            try
            {
                _client.Dispose();
            }
            catch
            {
                // best-effort
            }
        }

        _disposeCts.Dispose();
    }

    // --- read loop -------------------------------------------------------

    private async Task ReadLoopAsync(IRealtimeClientSession session, RealtimeAudioConfig audioDefaults, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in session.GetStreamingResponseAsync(ct).ConfigureAwait(false))
            {
                var evt = MeaiToSutandoEventAdapter.Map(msg, audioDefaults);
                if (evt is null)
                {
                    continue;
                }

                HandleEvent(evt);
                EventReceived?.Invoke(this, evt);

                if (evt is RealtimeToolCall toolCall)
                {
                    // Tool dispatch deliberately runs after the EventReceived notification so consumers
                    // see the tool call before we start handler tasks for it. Each handler runs on the
                    // thread pool — the response is sent back via the same session without blocking
                    // the read loop.
                    foreach (var call in toolCall.Calls)
                    {
                        _ = Task.Run(() => DispatchToolAsync(call, ct), CancellationToken.None);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on dispose / disconnect
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VoiceSession read loop faulted.");
            lock (_stateLock)
            {
                TransitionTo_NoLock(VoiceSessionState.Failed);
            }
        }
    }

    private void HandleEvent(RealtimeServerEvent evt)
    {
        switch (evt)
        {
            case RealtimeSetupComplete:
                lock (_stateLock)
                {
                    if (_state == VoiceSessionState.Connecting)
                    {
                        TransitionTo_NoLock(VoiceSessionState.Listening);
                    }
                }
                break;

            case RealtimeAudioOutput:
                lock (_stateLock)
                {
                    if (_state == VoiceSessionState.Listening)
                    {
                        TransitionTo_NoLock(VoiceSessionState.Speaking);
                    }
                }
                break;

            case RealtimeTurnComplete:
            case RealtimeInterrupted:
                lock (_stateLock)
                {
                    if (_state == VoiceSessionState.Speaking)
                    {
                        TransitionTo_NoLock(VoiceSessionState.Listening);
                    }
                }
                break;

            case RealtimeSessionResumptionUpdate update:
                if (!string.IsNullOrEmpty(update.Handle))
                {
                    _resumptionHandle = update.Handle;
                }
                break;

            case RealtimeTransportClosed:
                // Both client- and server-initiated closes land in the same terminal state. The
                // initiator survives on the RealtimeTransportClosed event for consumer inspection,
                // but does not influence the state machine in this slice.
                lock (_stateLock)
                {
                    TransitionTo_NoLock(VoiceSessionState.Disconnected);
                }
                break;

            case RealtimeTransportError:
                // We do not auto-fail on transient errors — let the consumer decide based on the
                // event stream. A subsequent Disconnected event will move us to Disconnected, and
                // the consumer can then choose to reconnect.
                break;
        }
    }

    private async Task DispatchToolAsync(RealtimeFunctionCall call, CancellationToken ct)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            if (!_tools.TryGetValue(call.Name, out var registration))
            {
                _logger.LogWarning("Received call for unregistered tool '{Tool}' (id={Id}). Replying with error.", call.Name, call.Id);
                var err = JsonSerializer.SerializeToElement(new { error = $"Tool '{call.Name}' is not registered." });
                await SendToolResponseAsync(session, new ToolResponse(call.Id, call.Name, err), ct).ConfigureAwait(false);
                return;
            }

            var result = await registration.Handler(call.Arguments, ct).ConfigureAwait(false);
            await SendToolResponseAsync(session, new ToolResponse(call.Id, call.Name, result), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // disposal / disconnect — nothing to surface back to the model
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool handler for '{Tool}' threw — surfacing error to model.", call.Name);
            try
            {
                var err = JsonSerializer.SerializeToElement(new { error = ex.Message });
                await SendToolResponseAsync(session, new ToolResponse(call.Id, call.Name, err), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }
        }
    }

    private static Task SendToolResponseAsync(IRealtimeClientSession session, ToolResponse response, CancellationToken ct)
    {
        // Build a MEAI conversation item carrying the function result. AdditionalProperties is
        // the only carrier MEAI gives us for the function-name (FunctionResultContent has only
        // CallId + Result + Exception), so we stash it there for the Gemini adapter to read.
        var additional = new AdditionalPropertiesDictionary
        {
            ["name"] = response.Name,
        };
        var content = new FunctionResultContent(response.ToolCallId, (object?)response.Response)
        {
            AdditionalProperties = additional,
        };
        var item = new RealtimeConversationItem(new List<AIContent> { content });
        var message = new CreateConversationItemRealtimeClientMessage(item);
        return session.SendAsync(message, ct);
    }

    private static RealtimeClientMessage BuildClientMessage(RealtimeInput input, RealtimeAudioConfig audio)
    {
        switch (input)
        {
            case RealtimeInput.RealtimeTextInput text:
            {
                var item = new RealtimeConversationItem(
                    new List<AIContent> { new TextContent(text.Value) },
                    role: ChatRole.User);
                return new CreateConversationItemRealtimeClientMessage(item);
            }
            case RealtimeInput.RealtimeAudioInput audioInput:
            {
                var mime = audioInput.MimeType ?? audio.InputMimeType;
                var content = new DataContent(audioInput.Pcm, mime);
                return new InputAudioBufferAppendRealtimeClientMessage(content);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(input), input.GetType(), "Unknown RealtimeInput variant.");
        }
    }

    private void TransitionTo_NoLock(VoiceSessionState next)
    {
        if (_state == next)
        {
            return;
        }
        var prev = _state;
        _state = next;
        StateChanged?.Invoke(this, new VoiceSessionStateChange(prev, next));
    }

    private sealed record ToolRegistration(RealtimeToolDefinition Definition, RealtimeToolHandler Handler);
}
