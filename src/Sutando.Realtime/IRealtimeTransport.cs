namespace Sutando.Realtime;

/// <summary>
/// Provider-agnostic surface for a bidirectional realtime LLM transport.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on bodhi's <c>LLMTransport</c> interface (Gemini / OpenAI Realtime / ElevenLabs). The
/// first concrete implementation is <see cref="GeminiLiveTransport"/>; future providers slot in
/// without changing <see cref="VoiceSession"/> or any consumer code.
/// </para>
/// <para>
/// <b>Lifecycle:</b> a transport is one-shot. After <see cref="DisconnectAsync"/> resolves, callers
/// MUST construct a new instance to reconnect (the transport may have held on to native resources
/// like a WebSocket which cannot be safely reused). <see cref="VoiceSession"/> implements reconnect
/// by allocating a fresh transport with the latest resumption handle.
/// </para>
/// <para>
/// <b>Threading:</b> <see cref="ConnectAsync"/> / <see cref="DisconnectAsync"/> may be invoked
/// concurrently with the other methods; the underlying SDK serialises sends, and event delivery is
/// funnelled through a single-producer channel.
/// </para>
/// </remarks>
public interface IRealtimeTransport : IAsyncDisposable
{
    /// <summary>
    /// Opens the WebSocket and sends the setup envelope. The returned task resolves once the WS
    /// handshake completes — <b>not</b> when the server replies with <see cref="RealtimeSetupComplete"/>.
    /// Wait for the latter on <see cref="ReadEventsAsync"/> to confirm the session is ready for input.
    /// </summary>
    /// <param name="config">Session configuration. The instance is captured and used for the duration of the connection.</param>
    /// <param name="ct">Cancellation token. Cancelling aborts an in-flight handshake.</param>
    /// <returns>A task that resolves when the underlying WebSocket has connected.</returns>
    Task ConnectAsync(RealtimeSessionConfig config, CancellationToken ct);

    /// <summary>
    /// Sends a single input — text or PCM audio chunk — to the model.
    /// </summary>
    /// <param name="input">The input to send.</param>
    /// <param name="ct">Cancellation token. Cancelling aborts the send.</param>
    Task SendRealtimeInputAsync(RealtimeInput input, CancellationToken ct);

    /// <summary>
    /// Returns a tool-call result to the model. Must be paired with an inbound <see cref="RealtimeToolCall"/>.
    /// </summary>
    /// <param name="response">The tool response. <see cref="ToolResponse.ToolCallId"/> must match the original call.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendToolResponseAsync(ToolResponse response, CancellationToken ct);

    /// <summary>
    /// Reads inbound server events as they arrive. The enumerable completes when the transport
    /// disconnects (whether voluntarily or by fault); the final event before completion is a
    /// <see cref="RealtimeTransportClosed"/>.
    /// </summary>
    /// <param name="ct">Cancellation token. Cancelling stops enumeration without disconnecting the transport.</param>
    IAsyncEnumerable<RealtimeServerEvent> ReadEventsAsync(CancellationToken ct);

    /// <summary>Closes the WebSocket cleanly.</summary>
    /// <param name="ct">Cancellation token. Cancelling abandons the close handshake.</param>
    Task DisconnectAsync(CancellationToken ct);
}
