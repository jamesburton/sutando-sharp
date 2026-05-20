using Microsoft.Extensions.AI;
using Sutando.Realtime;

namespace Sutando.Phone;

/// <summary>
/// DI-friendly factory wrapper around <see cref="IRealtimeClient"/>. Production registrations
/// build a <see cref="GeminiLiveRealtimeClient"/>; tests substitute an in-process fake so the
/// host can spin up without a real Gemini API key.
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
    /// Returns a freshly-allocated realtime client. The caller (the <see cref="VoiceSession"/>) owns it
    /// and disposes it when the session ends.
    /// </summary>
    /// <returns>A new <see cref="IRealtimeClient"/> instance.</returns>
    IRealtimeClient Create();
}

/// <summary>Default factory: returns a new <see cref="GeminiLiveRealtimeClient"/> on each call.</summary>
public sealed class GeminiLivePhoneTransportFactory : IPhoneTransportFactory
{
    /// <inheritdoc />
    public IRealtimeClient Create() => new GeminiLiveRealtimeClient();
}
