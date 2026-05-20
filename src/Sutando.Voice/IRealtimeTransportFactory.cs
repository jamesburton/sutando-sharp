using Microsoft.Extensions.AI;
using Sutando.Realtime;

namespace Sutando.Voice;

/// <summary>
/// DI-friendly factory wrapper around <see cref="IRealtimeClient"/>. Production registrations
/// build a <see cref="GeminiLiveRealtimeClient"/>; tests substitute an in-process fake so the
/// WS host can spin up without a real Gemini API key.
/// </summary>
/// <remarks>
/// Returns an <see cref="IRealtimeClient"/> — MEAI's reusable factory-of-sessions surface. The
/// host calls <see cref="Create"/> once per browser WebSocket; the resulting client is handed
/// to a <see cref="VoiceSession"/> and disposed when the session ends. Test fakes capture the
/// returned client so they can pump events into the (single) session it minted.
/// </remarks>
public interface IRealtimeTransportFactory
{
    /// <summary>
    /// Returns a freshly-allocated realtime client. The caller (the <see cref="VoiceSession"/>) owns it
    /// and disposes it when the session ends.
    /// </summary>
    /// <returns>A new <see cref="IRealtimeClient"/> instance.</returns>
    IRealtimeClient Create();
}

/// <summary>Default factory: returns a new <see cref="GeminiLiveRealtimeClient"/> on each call.</summary>
public sealed class GeminiLiveTransportFactory : IRealtimeTransportFactory
{
    /// <inheritdoc />
    public IRealtimeClient Create() => new GeminiLiveRealtimeClient();
}
