# Sutando.Voice.Local — Integration Notes

`Sutando.Voice.Local` is the **`sutando voice --local`** transport — phase #21 of the
local-inference stack (`docs/local-stack-scope.md §7`). It lets the voice WebSocket server run a
connection end-to-end on the developer's machine — browser audio in → STT → Chat → TTS → audio
out — with no cloud calls.

This document records how the project plugs into the rest of the solution, what the follow-up
integration phase has to wire up, and the load-bearing design decisions, so the next contributor
can pick it up without re-deriving the rationale from commit history.

## What this project is

The whole transport is **one `IRealtimeClient` implementation**. The voice WS server already
drives every connection through `Sutando.Realtime`'s `VoiceSession`, which wraps an
`IRealtimeClient` handed out by `IRealtimeTransportFactory`. Phase #21 does **not** add a parallel
WS handler — it swaps *which* `IRealtimeClient` the factory mints:

| Type | Role |
|---|---|
| `LocalPipelineRealtimeClient` | `IRealtimeClient` — mints one session per browser connection. |
| `LocalPipelineRealtimeClientSession` | `IRealtimeClientSession` — owns one `Sutando.Pipeline` run. |
| `ChannelPipelineSource` | Pipeline **source** — the session writes inbound browser frames into its channel. |
| `RealtimeEventSink` | Pipeline **sink** — translates pipeline frames into MEAI `RealtimeServerMessage`s. |
| `LocalPipelineOptions` | The four pluggable stage components + tuning knobs. |

The pipeline shape inside the session is:

```
ChannelPipelineSource → VadStage → SpeechToTextStage → ChatStage → TextToSpeechStage → RealtimeEventSink
```

All five middle stages are reused verbatim from `Sutando.Pipeline` — no stage logic or
interruption convention is reinvented here.

## Solution wiring

`Sutando.sln` is **deliberately unmodified** in this slice — per the task brief. The new project
is pulled into `dotnet build Sutando.sln` transitively:

- `src/Sutando.Voice/Sutando.Voice.csproj` has a `<ProjectReference>` to `Sutando.Voice.Local`
  (the voice host is the real consumer), plus references to the four in-process local-inference
  adapters (`WhisperNet`, `KokoroSharp`, `LlamaSharp`, `Silero`) used by `LocalPipelineBootstrap`.
- `tests/Sutando.Tests/Sutando.Tests.csproj` references `Sutando.Voice.Local` directly so the
  tests can construct its types.

When the follow-up integration phase wires this in, add the project to `Sutando.sln` with:

```pwsh
dotnet sln Sutando.sln add src/Sutando.Voice.Local/Sutando.Voice.Local.csproj
```

## CLI wiring

The `voice` verb gains a `--local` switch:

- `src/Sutando.Cli/Program.cs` — help text updated to document `voice [--local]`.
- `src/Sutando.Cli/Commands.cs` — unchanged: `VoiceAsync` already forwards every arg after the
  verb to `VoiceCommand.RunAsync`, so `--local` reaches `VoiceServer.Build` untouched.
- `src/Sutando.Voice/VoiceServer.cs` — `ResolveUseLocal` parses `--local` (CLI) /
  `SUTANDO_VOICE_LOCAL` (env) / `Voice:UseLocal` (config), in that precedence. When local mode is
  on, `VoiceServer.Build` registers `LocalPipelineTransportFactory` instead of
  `GeminiLiveTransportFactory`.
- `src/Sutando.Voice/VoiceOptions.cs` — new `UseLocal` flag and `LocalModelPaths` (the four model
  file paths, resolved from `SUTANDO_WHISPER_MODEL` / `SUTANDO_LLAMA_MODEL` /
  `SUTANDO_KOKORO_MODEL` / `SUTANDO_SILERO_MODEL`).
- `src/Sutando.Voice/VoiceWebSocketHandler.cs` — the Gemini API-key gate is now skipped in local
  mode (there is no API key); the local transport surfaces its own config errors.

## The two flavours — `local-stack-scope.md §7`

§7 calls for two flavours sharing one `LocalPipelineTransport`:

1. **Pure in-process (laptop)** — Whisper.net / KokoroSharp / LlamaSharp / Silero, all loaded
   from local model files. **This flavour is implemented end-to-end.** `LocalPipelineBootstrap`
   (in `Sutando.Voice`) resolves the four model files from env vars and assembles a
   `LocalPipelineOptions`; `LocalPipelineTransportFactory` builds it once at server boot.

2. **AppHost-orchestrated (GPU workstation)** — the chat / STT / TTS clients are HTTP clients
   (`Sutando.LocalInference.OpenAI`'s `AddOpenAiCompatible*`) pointed at vLLM / speaches /
   kokoro-fastapi running as containers under `Sutando.AppHost`; VAD stays in-process.
   **This flavour is deferred — see "Deferred" below.**

**The shared abstraction.** `LocalPipelineOptions` is the seam: it holds an `IVadDetector`, an
`ISpeechToTextClient`, an `IChatClient`, and an `ITextToSpeechClient` as plain interface
references. Neither `LocalPipelineRealtimeClient` nor the pipeline shape changes between flavours
— only which concrete clients land in those four slots. The AppHost flavour plugs in by writing
a second bootstrap that builds `LocalPipelineOptions` from the OpenAI-compatible HTTP clients;
`LocalPipelineTransportFactory` is otherwise unchanged.

## Frame ↔ MEAI message mapping

`RealtimeEventSink.MapFrame` is the load-bearing translation. Every row is verified against
`Sutando.Realtime.MeaiToSutandoEventAdapter.Map`, which is what the voice WS server's
`VoiceSession` read-loop runs the messages through before raising `RealtimeServerEvent`s:

| Pipeline frame | MEAI message | → `RealtimeServerEvent` | → browser envelope |
|---|---|---|---|
| (session start) | `RealtimeServerMessage{SessionStarted}` | `RealtimeSetupComplete` | `setup_complete` |
| `AudioOutputFrame` | `OutputTextAudio{OutputAudioDelta}` (base64 PCM) | `RealtimeAudioOutput` | `audio` |
| `TextFrame{IsFinal:true}` | `OutputTextAudio{InputAudioTranscriptionCompleted}` | `RealtimeInputTranscription` | `input_transcription` |
| `TextFrame{IsFinal:false}` | `OutputTextAudio{OutputAudioTranscriptionDelta}` | `RealtimeOutputTranscription` | `output_transcription` |
| `ControlFrame{TurnComplete}` | `ResponseCreated{ResponseDone}` | `RealtimeTurnComplete` | `turn_complete` |
| `ControlFrame{Interrupt}` | `ResponseCreated{ResponseDone, Cancelled}` | `RealtimeInterrupted` | `interrupted` |
| pipeline fault | `ErrorRealtimeServerMessage` | `RealtimeTransportError` | `error` |

`VadFrame`s and `Start`/`Stop` control frames have no browser representation and are dropped — the
same "transparent composition" discard `WebSocketAudioSink` applies to non-audio frames.

## Load-bearing design decisions

- **`LocalPipelineRealtimeClient` IS the `LocalPipelineTransport`.** The spec names a
  "LocalPipelineTransport"; in MEAI's converged world the transport seam *is* `IRealtimeClient`.
  Implementing that interface — rather than inventing a new abstraction — is what lets the voice
  WS server stay untouched and the browser wire protocol be preserved for free.

- **A custom channel-backed source, not `WebSocketAudioSource`.** `WebSocketAudioSource` reads
  raw PCM bytes off an `IAudioByteStream` and only ever produces `AudioInputFrame`s. The local
  transport also has to inject user *text* turns (the browser's `text` envelope) as final
  `TextFrame`s, and it already holds decoded `AudioFrame`s rather than a byte stream.
  `ChannelPipelineSource` accepts arbitrary `PipelineFrame`s through a channel the session writes
  into — the natural fit. `WebSocketAudioSource` is left in place for byte-stream consumers.

- **A custom sink (`RealtimeEventSink`), not `WebSocketAudioSink`.** `WebSocketAudioSink` writes
  raw PCM bytes to a byte-stream transport. For the local voice transport the real "transport" is
  the `IRealtimeClientSession` event stream — the voice WS server consumes `RealtimeServerMessage`s,
  not raw bytes. So this sink emits MEAI messages instead. Both sinks coexist because they target
  different transports — see the XML doc on `RealtimeEventSink` for the same note.

- **Lazy pipeline start.** The pipeline is built and run on the first `SendAsync` /
  `GetStreamingResponseAsync`, mirroring `GeminiLiveRealtimeClientSession`'s lazy connect. The
  `SutandoSessionStarted` message is emitted immediately on start so the WS handshake completes
  before any audio is produced.

- **Fail-graceful, not fail-fast.** Model files are validated at server boot
  (`LocalPipelineBootstrap.RequireModelFile` → `File.Exists`). A missing file does **not** crash
  the host: `LocalPipelineTransportFactory` catches `LocalPipelineConfigurationException` and
  every session becomes a `LocalPipelineRealtimeClient.Unavailable(...)` — the browser completes
  the handshake then receives a clear `error` envelope. This mirrors the existing missing-API-key
  path for Gemini mode. `/healthz` and the harness page stay up regardless.

- **Interruption** rides the existing `Sutando.Pipeline` convention — `ControlFrame.Interrupt`
  flows in-band; each stage cancels its per-turn CTS. The local transport does not add a new
  interruption mechanism. (See "Deferred" for client-initiated barge-in.)

## Deferred

- **AppHost-orchestrated flavour.** Only the pure in-process flavour is wired. The AppHost
  flavour needs a second bootstrap building `LocalPipelineOptions` from
  `Sutando.LocalInference.OpenAI`'s HTTP clients (endpoints discovered from the `Sutando.AppHost`
  Aspire graph), plus a `Voice:RemoteEndpoints` config section. `LocalPipelineRealtimeClient`,
  `LocalPipelineTransportFactory`, the pipeline, and the wire protocol need **no change** — only
  the bootstrap. Tracked as a follow-up to phase #21.

- **Client-initiated barge-in.** The browser's `interrupt` / `end_turn` envelopes are still
  logged-only in `VoiceWebSocketHandler` (pre-existing deferral — `RealtimeInput` exposes no
  interrupt variant). The local pipeline's interruption is driven by VAD `SpeechStart` instead,
  which is the natural mechanism for a voice loop. Wiring the explicit envelopes through would
  touch `Sutando.Realtime`'s `RealtimeInput` / `VoiceSession` and is out of scope here.

- **Model tool-calls.** `LocalPipelineRealtimeClientSession.SendAsync` ignores tool-result
  client messages — the local pipeline has no tool-dispatch stage in this slice. `ChatStage`
  already emits `ToolCallFrame`s; a future tool-runner stage would slot between `ChatStage` and
  `TextToSpeechStage` without touching this transport.

## Constraints honoured

- `Sutando.sln` is unchanged (transitive references only).
- The voice WS handler, the wire envelopes, and `/healthz` are unchanged.
- All tests pass on both `net10.0` and `net10.0-windows10.0.19041.0`.
- Tests use fakes for the four stage components — no real model downloads.
- `MEAI001` + `SUTANDO001` suppressed at the csproj level so consumers don't see per-call
  warnings.
