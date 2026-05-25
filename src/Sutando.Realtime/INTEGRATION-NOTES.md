# Sutando.Realtime — Integration Notes

> **⚠️ Partially superseded.** The sections below describe the original bespoke
> `IRealtimeTransport` / `GeminiLiveTransport` slice. `Sutando.Realtime` has since
> converged onto Microsoft.Extensions.AI's `IRealtimeClient` / `IRealtimeClientSession`
> — `IRealtimeTransport` and `GeminiLiveTransport` no longer exist. For the current
> design (type-by-type map, server-event map, deviations from MEAI's contract), see
> [`MAPPING.md`](./MAPPING.md), which is the source of truth. The "Solution wiring",
> "Dependency", "What's DEFERRED" and "Reconnect semantics" sections below remain
> broadly accurate; the "What's IN scope" type list does not.

This document tracks how `Sutando.Realtime` plugs into the rest of the solution and what
the follow-up phase still has to wire up. It exists so the next contributor can
pick up the file without re-deriving the rationale from commit history.

## Solution wiring

`Sutando.sln` is **deliberately unmodified** in this slice. The new project is pulled into
`dotnet build Sutando.sln` transitively through `tests/Sutando.Tests/Sutando.Tests.csproj`,
which now includes:

```xml
<ProjectReference Include="..\..\src\Sutando.Realtime\Sutando.Realtime.csproj" />
```

When the follow-up phase wires `Sutando.Realtime` into the channels stack (e.g. as a
`Sutando.Channels.Voice` project that drives a `VoiceSession`), `Sutando.Realtime` should be
added to `Sutando.sln` directly at that point. Until then, the transitive path keeps both
build and test on the happy path.

## Dependency: Google_GenerativeAI.Live

We use the `Google_GenerativeAI.Live` v3.6.6 NuGet (April 2026 release). It explicitly targets
.NET 6–9 but loads cleanly under .NET 10 — verified by a full build and the 11
payload-mapping unit tests. The bodhi scope report singled this package out as the discriminator
that makes the port "medium" instead of "large"; in practice the SDK is well-shaped — it gives
us `MultiModalLiveClient` with typed events for every wire message we care about
(`AudioChunkReceived`, `MessageReceived`, `InputTranscriptionReceived`, `OutputTranscriptionReceived`,
`GoAwayReceived`, `SessionResumableUpdateReceived`, `Connected`, `Disconnected`, `ErrorOccurred`,
`GenerationInterrupted`).

There is one quirk worth noting for future work: the SDK auto-executes tool calls when its
`FunctionTools` collection is populated (via a private `CallFunctionsWithErrorHandlingAsync`
method). We deliberately leave that collection empty and dispatch tool calls ourselves from
`MessageReceived` → `BidiResponsePayload.ToolCall` so the consumer keeps full control over
execution semantics (including the `Inline | Background` distinction modelled on
`RealtimeToolDefinition.Execution`).

## What's IN scope

`Sutando.Realtime` is a provider-agnostic realtime voice client converged onto
Microsoft.Extensions.AI's `IRealtimeClient` / `IRealtimeClientSession`, plus the
consumer-facing `VoiceSession` orchestration layer:

- `GeminiLiveRealtimeClient` / `GeminiLiveRealtimeClientSession` — the Gemini Live
  adapter, split along MEAI's client/session boundary (the client holds auth + model id;
  each `CreateSessionAsync` mints a per-WebSocket session).
- `RealtimeSessionConfig`, `RealtimeAudioConfig`, `RealtimeInput`, `ToolResponse`,
  `RealtimeToolDefinition`, `RealtimeToolHandler` — the consumer-facing record/delegate surface.
- `RealtimeServerEvent` — the consumer-facing discriminated union (setup-complete,
  audio-output, input/output transcription, interrupted, turn-complete, grounding-metadata,
  tool-call, tool-call-cancellation, go-away, session-resumption-update, transport-closed,
  transport-error); `MeaiToSutandoEventAdapter` translates MEAI server messages into it.
- `VoiceSession` with the `Idle → Connecting → Listening ⇄ Speaking → Disconnected | Failed`
  state machine, state-change event, tool-call dispatch, and resumption-handle caching for
  the next reconnect.
- Unit tests for JSON-frame → event mapping (`GeminiPayloadMapperTests`) and for the state
  machine + tool dispatch via an in-process fake (`VoiceSessionStateMachineTests`), plus a
  `[Fact(Skip = ...)]` integration test (`GeminiLiveIntegrationTests`) — flip the `Skip`
  and set `GEMINI_API_KEY` to validate end-to-end against the real endpoint.

For the exact type-by-type and event-by-event mapping onto MEAI — and the deliberate
deviations from MEAI's contract — see [`MAPPING.md`](./MAPPING.md), the source of truth
for the current design.

## What's DEFERRED to the follow-up phase

These are bodhi-realtime-agent features the scope report flagged as `VoiceSession`-level
orchestration. They are explicitly not in the transport-only slice.

1. **WebSocket server on `:9900`** — bodhi exposes a `ws` server so its browser/CLI client can
   stream audio in and receive audio out. Our `VoiceSession` is consumer-facing only; no
   network listener is bound. The follow-up phase wires a Kestrel WS handler that fans out
   to/from `VoiceSession`.
2. **Web client** — the `web-client.ts` in bodhi's `examples/` is not ported. We expect a
   `Sutando.Channels.Voice` package to ship later with either a Blazor UI or a thin HTML/JS
   client mirroring bodhi's.
3. **Audio device IO** — capturing from a microphone / playing back to speakers is host-
   specific (NAudio on Windows, ALSA on Linux, CoreAudio on Mac). Not in scope; consumers feed
   `RealtimeInput.Audio(...)` from whatever source they have.
4. **Audio file ingestion** — bodhi has helpers for sending pre-recorded WAV files. We do not
   ship those; the same `RealtimeInput.Audio(...)` path covers it once the consumer reads the
   bytes off disk.
5. **Conversation replay on reconnect** — bodhi maintains a `ConversationContext.ReplayItem[]`
   and re-sends it on reconnect when the session-resumption handle is unavailable. We capture
   the handle and pass it back into the next `ConnectAsync`, but we do **not** replay
   conversation items. The scope report calls out that this is the area with the most
   "fix/reconnect-promise-deadline"-style scars in bodhi's history; tackling it requires a
   `ConversationContext` model that this slice deliberately omits.
6. **Subagent runtime** — bodhi's background tools dispatch into a Vercel-AI-SDK subagent
   loop. Our `RealtimeToolDefinition.Execution` enum (`Inline | Background`) is the API hook
   for that future runtime; right now both kinds dispatch identically (the value is preserved
   so the surface doesn't break when the subagent path lands).
7. **OpenAI Realtime / ElevenLabs STT transports** — only `GeminiLiveTransport` is provided.
   `IRealtimeTransport` is provider-agnostic, so adding these later is additive.

## Skill-tool bridge

> Adapter + bridge live in `Sutando.Voice/Skills/`, not in `Sutando.Realtime`. Rationale:
> `Sutando.Phone` also depends on `Sutando.Realtime`, and pulling a `Sutando.Skills` reference
> in here would force a transitive dependency Phone does not need. Voice is the natural seam —
> it already owns `IRealtimeTransportFactory` and the WS handler that mints sessions.

The bridge ports the concept of upstream's `loadSkillManifestTools()` to the .NET surface:
turn each registered `ISkill` into a `RealtimeToolDefinition` the model sees, plus a
`RealtimeToolHandler` delegate that routes inbound tool calls back to `ISkill.ExecuteAsync`.

- **`SkillVoiceTool`** (`src/Sutando.Voice/Skills/SkillVoiceTool.cs`) — single-skill adapter.
  Tool name = `manifest.Id` (validated against Gemini's `[a-zA-Z0-9_-]{1,63}` rule, throws
  `ArgumentException` otherwise). Default parameter schema is a permissive
  `{"type":"object","additionalProperties":{"type":"string"}}` — mirrors what `ISkill` actually
  accepts. Per-skill schema overrides are supported via the ctor's `parameterSchemaOverride`.
  Arguments are coerced JSON → `IReadOnlyDictionary<string, string>`: strings flow through;
  non-strings survive as their raw JSON text so structured skills can re-parse.

- **`SkillRegistryVoiceBridge`** (same folder) — snapshot of a `SkillRegistry` into a name →
  adapter map. Exposes `GetToolDefinitions()` (feed into `RealtimeSessionConfig.Tools`),
  `TryGetHandler(name)` (dispatcher), and `RegisterWith(session)` (one-call wiring against a
  `VoiceSession` before `ConnectAsync`).

- **DI extension** — `SkillRegistryVoiceBridgeServiceCollectionExtensions.AddSkillRegistryVoiceBridge()`
  builds a singleton bridge from the container's `SkillRegistry` + `WorkspaceDirectory`. An overload
  accepts a pre-built bridge for callers that need a custom schema resolver or HTTP client.

- **Host wiring** — `VoiceWebSocketHandler` takes an optional `SkillRegistryVoiceBridge?` ctor
  parameter (defaulted `null`). When non-null and non-empty, the handler advertises the bridge's
  tools on the session config and registers every handler against the session before
  `ConnectAsync`. **No registration = no behavioural change** — both the cloud and `--local`
  voice paths are identical to the pre-bridge slice when the integrator hasn't opted in.

### Integrator wiring (CLI follow-up)

The CLI's `sutando voice` verb is the natural seat. The integration commit there should:

1. Build a `SkillRegistry` via the existing `SkillsHost.BuildRegistry(workspace)` helper.
2. Construct a `SkillRegistryVoiceBridge(registry, workspace)`.
3. Register both `SkillRegistry` and the bridge with the voice host's `IServiceCollection`
   before calling `VoiceServer.Build(args).RunAsync()`. The DI extension method above is the
   one-liner.
4. Surface an opt-in flag (suggested: `--skills` / `--no-skills`) so operators can skip the
   bridge when they want plain voice.

### Follow-ups

- **Schema slot on `SkillManifest`.** A first-class JSON-Schema field would let on-disk skills
  declare typed parameters without a per-host resolver. Out of scope for this slice (Skills is
  off-limits); the `Func<SkillManifest, JsonElement?>` resolver hook on the bridge is the
  workaround in the meantime.
- **Subagent dispatch.** `RealtimeToolDefinition.Execution` ships as `Inline` for every
  skill-derived tool. Wiring `Background` to a real runner is the same follow-up referenced in
  the deferred-work section above.

## Reconnect semantics in this slice

`VoiceSession` is **not** auto-reconnecting. When a non-client-initiated
`RealtimeTransportClosed` arrives, the session moves to `Disconnected` and stops. The
consumer (or the deferred orchestrator) is responsible for calling `ConnectAsync` again. The
cached `ResumptionHandle` is automatically re-applied to the next config — i.e. you can
simply pass the same `RealtimeSessionConfig` back in and the session will hand the handle to
the server. No conversation-item replay; no exponential-backoff loop. That last bit is the
deferred piece — it lives at the orchestrator layer, not the transport layer.
