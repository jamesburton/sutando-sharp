using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sutando.Realtime;

/// <summary>
/// High-level wrapper around an <see cref="IRealtimeTransport"/>. Owns the conversation event bus,
/// the lifecycle state machine, and tool-call dispatch.
/// </summary>
/// <remarks>
/// <para>
/// This is the surface bodhi exposes as <c>VoiceSession</c>; the C# implementation here is the
/// first slice — transport + state machine + tool dispatch. The deferred work is captured in
/// <c>INTEGRATION-NOTES.md</c> and includes the WebSocket fan-out server (bodhi binds <c>:9900</c>
/// for its browser client), the web client itself, audio device IO, audio-file ingestion, and the
/// full conversation-replay reconnect path.
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
    private readonly Func<IRealtimeTransport> _transportFactory;
    private readonly ILogger<VoiceSession> _logger;
    private readonly Dictionary<string, ToolRegistration> _tools = new(StringComparer.Ordinal);
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _disposeCts = new();

    private VoiceSessionState _state = VoiceSessionState.Idle;
    private IRealtimeTransport? _transport;
    private RealtimeSessionConfig? _config;
    private Task? _readLoop;
    private string? _resumptionHandle;
    private bool _disposed;

    /// <summary>Creates a new session.</summary>
    /// <param name="transportFactory">
    /// Factory invoked once per connect — including reconnects — to allocate a fresh
    /// <see cref="IRealtimeTransport"/>. The default factory builds a <see cref="GeminiLiveTransport"/>
    /// with the supplied <paramref name="logger"/>.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public VoiceSession(
        Func<IRealtimeTransport>? transportFactory = null,
        ILogger<VoiceSession>? logger = null)
    {
        _logger = logger ?? NullLogger<VoiceSession>.Instance;
        _transportFactory = transportFactory ?? (() => new GeminiLiveTransport());
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
    /// Connects the underlying transport and starts the read-loop. Completes when the WebSocket
    /// handshake succeeds — callers should wait for <see cref="State"/> to reach
    /// <see cref="VoiceSessionState.Listening"/> (which fires on the <see cref="RealtimeSetupComplete"/>
    /// event) before sending input.
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

        var transport = _transportFactory();
        _transport = transport;
        _config = effective;

        try
        {
            await transport.ConnectAsync(effective, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_stateLock)
            {
                TransitionTo_NoLock(VoiceSessionState.Failed);
            }
            await transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
            throw;
        }

        _readLoop = Task.Run(() => ReadLoopAsync(transport, _disposeCts.Token), CancellationToken.None);
    }

    /// <summary>Sends a single realtime input (text or audio).</summary>
    /// <param name="input">The input.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task SendAsync(RealtimeInput input, CancellationToken ct)
    {
        var transport = _transport ?? throw new InvalidOperationException("Session is not connected.");
        return transport.SendRealtimeInputAsync(input, ct);
    }

    /// <summary>Disconnects the underlying transport.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        var transport = _transport;
        if (transport is null)
        {
            return;
        }

        await transport.DisconnectAsync(ct).ConfigureAwait(false);
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

        if (_transport is { } transport)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
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

        _disposeCts.Dispose();
    }

    // --- read loop -------------------------------------------------------

    private async Task ReadLoopAsync(IRealtimeTransport transport, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in transport.ReadEventsAsync(ct).ConfigureAwait(false))
            {
                HandleEvent(evt);
                EventReceived?.Invoke(this, evt);

                if (evt is RealtimeToolCall toolCall)
                {
                    // Tool dispatch deliberately runs after the EventReceived notification so consumers
                    // see the tool call before we start handler tasks for it. Each handler runs on the
                    // thread pool — the response is sent back via the same transport without blocking
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
        var transport = _transport;
        if (transport is null)
        {
            return;
        }

        try
        {
            if (!_tools.TryGetValue(call.Name, out var registration))
            {
                _logger.LogWarning("Received call for unregistered tool '{Tool}' (id={Id}). Replying with error.", call.Name, call.Id);
                var err = JsonSerializer.SerializeToElement(new { error = $"Tool '{call.Name}' is not registered." });
                await transport.SendToolResponseAsync(new ToolResponse(call.Id, call.Name, err), ct).ConfigureAwait(false);
                return;
            }

            var result = await registration.Handler(call.Arguments, ct).ConfigureAwait(false);
            await transport.SendToolResponseAsync(new ToolResponse(call.Id, call.Name, result), ct).ConfigureAwait(false);
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
                await transport.SendToolResponseAsync(new ToolResponse(call.Id, call.Name, err), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }
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
