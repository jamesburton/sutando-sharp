using System.Runtime.CompilerServices;

// Tests need access to the GeminiPayloadMapper static helper so they can feed deserialised wire
// frames in without standing up a real WebSocket. Production callers go through IRealtimeTransport.
[assembly: InternalsVisibleTo("Sutando.Tests")]
