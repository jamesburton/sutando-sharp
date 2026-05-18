using System.Runtime.CompilerServices;

// Tests assert on internal wire envelopes (ClientMessage / ServerMessage) and on
// VoiceWebSocketHandler.MapEvent so they can verify the RealtimeServerEvent → JSON projection
// without standing the host up. Production callers never touch those types.
[assembly: InternalsVisibleTo("Sutando.Tests")]
