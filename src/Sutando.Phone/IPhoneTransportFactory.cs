using Sutando.Realtime;

namespace Sutando.Phone;

/// <summary>
/// DI-friendly factory wrapper around <see cref="IRealtimeTransport"/>. Production
/// registrations build a <see cref="GeminiLiveTransport"/>; tests substitute an in-process
/// fake so the host can spin up without a real Gemini API key.
/// </summary>
/// <remarks>
/// <para>
/// Intentionally a peer of <c>Sutando.Voice.IRealtimeTransportFactory</c> rather than a
/// reference to it. The two services have identical needs but the phone bridge does not
/// otherwise depend on <c>Sutando.Voice</c>; replicating this tiny interface keeps the
/// dependency graph DAG-shaped (Voice and Phone are siblings under Realtime).
/// </para>
/// </remarks>
public interface IPhoneTransportFactory
{
    /// <summary>
    /// Returns a freshly-allocated transport. The caller (the <see cref="VoiceSession"/>) owns it
    /// and disposes it when the session ends.
    /// </summary>
    /// <returns>A new <see cref="IRealtimeTransport"/> instance.</returns>
    IRealtimeTransport Create();
}

/// <summary>Default factory: returns a new <see cref="GeminiLiveTransport"/> on each call.</summary>
public sealed class GeminiLivePhoneTransportFactory : IPhoneTransportFactory
{
    /// <inheritdoc />
    public IRealtimeTransport Create() => new GeminiLiveTransport();
}
