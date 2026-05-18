using Sutando.Realtime;

namespace Sutando.Voice;

/// <summary>
/// DI-friendly factory wrapper around <see cref="IRealtimeTransport"/>. Production registrations
/// build a <see cref="GeminiLiveTransport"/>; tests substitute an in-process fake so the WS host
/// can spin up without a real Gemini API key.
/// </summary>
public interface IRealtimeTransportFactory
{
    /// <summary>
    /// Returns a freshly-allocated transport. The caller (the <see cref="VoiceSession"/>) owns it
    /// and disposes it when the session ends.
    /// </summary>
    /// <returns>A new <see cref="IRealtimeTransport"/> instance.</returns>
    IRealtimeTransport Create();
}

/// <summary>Default factory: returns a new <see cref="GeminiLiveTransport"/> on each call.</summary>
public sealed class GeminiLiveTransportFactory : IRealtimeTransportFactory
{
    /// <inheritdoc />
    public IRealtimeTransport Create() => new GeminiLiveTransport();
}
