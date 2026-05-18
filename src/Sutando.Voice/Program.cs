// Sutando.Voice — entry point.
//
// Minimal-API host that fans out a Gemini Live voice session per WebSocket client. The bulk of
// the wiring lives in VoiceServer.Build(args) so tests can stand the host up via
// WebApplicationFactory<Program> without re-implementing the topology.

using Sutando.Voice;

var app = VoiceServer.Build(args);
await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Public marker so <c>WebApplicationFactory&lt;Program&gt;</c> can locate the host's entry
/// assembly. Top-level statements compile to an internal synthetic class — the partial below
/// promotes it to public.
/// </summary>
public partial class Program;
