# Sutando.Voice — Integration Notes

This document captures the wiring the follow-up phase needs to perform once we're ready to register
`Sutando.Voice` in the solution and CLI. `Sutando.sln` and `Sutando.Cli` are **intentionally
unmodified** in this slice — the new project is reachable through the test project's
`<ProjectReference>` and via direct `dotnet build src/Sutando.Voice/Sutando.Voice.csproj`.

## Solution wiring

```pwsh
dotnet sln Sutando.sln add src/Sutando.Voice/Sutando.Voice.csproj
```

Place the project alongside the other `Sutando.*` source projects in the solution folder.

## CLI verb

Add the `voice` subcommand to `src/Sutando.Cli/Sutando.Cli.csproj`:

```xml
<ProjectReference Include="..\Sutando.Voice\Sutando.Voice.csproj" />
```

…then thread it into `src/Sutando.Cli/Commands.cs` and `src/Sutando.Cli/Program.cs` mirroring
the `browser` verb:

```csharp
// src/Sutando.Cli/Commands.cs
public static async Task<int> VoiceAsync(string[] args)
{
    // Drop the leading "voice" verb before forwarding to the shim.
    var forwarded = args.Length > 1 ? args[1..] : [];
    using var cts = NewSigIntCts();
    return await VoiceCommand.RunAsync(forwarded, ct: cts.Token).ConfigureAwait(false);
}

// src/Sutando.Cli/Program.cs — add to the switch in the args[0] dispatch:
"voice" => await Commands.VoiceAsync(args).ConfigureAwait(false),

// …and to PrintHelp:
Console.WriteLine("  voice [--port <n>]        Run the realtime voice WS server (Gemini Live fan-out).");
```

The `voice` verb forwards `--port <n>` straight to `VoiceServer.Build(args)`.

## Configuration precedence

`VoiceOptions.Port` is resolved in this order (highest precedence first):

1. CLI args: `--port <n>` or `--port=<n>`.
2. Environment: `SUTANDO_VOICE_PORT`.
3. Configuration: `Voice:Port` (appsettings / env `Voice__Port`).
4. Default: `9900` (matches upstream).

`VoiceOptions.ApiKey` comes from `GEMINI_VOICE_API_KEY`, falling back to `GEMINI_API_KEY`. Missing
keys are not fatal at startup — the WS handler refuses each `/voice` upgrade with a JSON
`{"type":"error","message":"…"}` envelope and an HTTP 1008 close so the operator gets a clear
signal instead of a doomed Gemini handshake.

## Wire protocol

The browser ↔ server JSON envelope is defined in
[`docs/voice-wire-protocol.md`](docs/voice-wire-protocol.md). Note that this **diverges from
upstream sutando** — upstream uses binary WS frames for audio and a different (`transcript`,
`turn.end`, `session.config`, etc.) event vocabulary. We chose the JSON-only envelope from the
spec because it gives a cleaner mapping over our `RealtimeServerEvent` discriminated union and
keeps the dev harness easy to inspect. A future production client compat layer can sit on top.

## Deferred work

Inheriting the deferred list from `src/Sutando.Realtime/INTEGRATION-NOTES.md` and adding the
voice-server-specific items:

- **Web client.** The page served at `/` is a developer harness — three buttons and a log pane.
  The full upstream `web-client.ts` UI is not ported.
- **Microphone capture / speaker playback.** The harness page does not access `getUserMedia` or
  `AudioContext`; it only exists to confirm the WS handshake and JSON round-trip work. Real audio
  capture / playback lives in the deferred web client.
- **Client-initiated `interrupt` / `end_turn` envelopes.** Accepted on the wire (and logged at
  debug) but not forwarded. `IRealtimeTransport` is fixed and exposes no interrupt or
  explicit-turn-complete API; threading those through requires modifying the realtime layer.
- **Reconnect strategy.** Each WS connection owns one `VoiceSession`. If Gemini disconnects, the
  client closes; the browser retries by opening a new socket. No backoff loop, no resumption-handle
  replay on the server side beyond what `VoiceSession` already caches internally.
- **Multi-session orchestration.** The handler runs one session per WS connection — there is no
  shared conversation context, no per-user session map, no IAM/auth gating. The follow-up
  channel-voice phase wires those in.
- **TLS.** The host binds plain HTTP (`http://0.0.0.0:<port>`). TLS termination is left to the
  reverse proxy / dev tunnel in front of `sutando voice`.
