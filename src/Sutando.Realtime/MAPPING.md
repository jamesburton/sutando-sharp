# Sutando.Realtime ↔ Microsoft.Extensions.AI realtime — mapping notes

Captures the convergence of `Sutando.Realtime` onto MEAI 10.6's
`IRealtimeClient` / `IRealtimeClientSession` surface (the follow-up tracked in
`docs/agent-framework-scope.md` § 5). The migration is internal —
`Sutando.Voice` and `Sutando.Phone` continue to consume `VoiceSession` and the
existing `RealtimeServerEvent` discriminated union, so the consumer-facing
surface stays stable.

## Type-by-type map

| Old (`Sutando.Realtime`) | New (MEAI / Sutando) | Notes |
|---|---|---|
| `IRealtimeTransport` (per-connection) | `IRealtimeClientSession` | Retired. Direct rename in spirit — both are one-shot, async-disposable, send + receive-stream. |
| `GeminiLiveTransport` (auth + WS in one) | `GeminiLiveRealtimeClient : IRealtimeClient` (auth) + `GeminiLiveRealtimeClientSession : IRealtimeClientSession` (per-WS) | Split along MEAI's client/session boundary. The client is reusable and holds API key + model id; each `CreateSessionAsync` mints a fresh session. |
| `IRealtimeTransportFactory` (Voice) / `IPhoneTransportFactory` (Phone) | `IRealtimeClient` (singleton, registered in DI) | The factory pattern collapses — MEAI clients are themselves factories of sessions. The two adapter projects now register a singleton `IRealtimeClient` directly. The legacy interfaces stay as thin compatibility shims that return the singleton on `Create()` — keeps the in-process test fakes ergonomic. |
| `RealtimeSessionConfig` | Slimmed-down + `RealtimeSessionOptions` | `ApiKey` moves off the config (lives on the client now). `Model`, `Voice`, `SystemInstruction`, `Tools`, `EnableInput/OutputTranscription`, `ResumptionHandle` stay on `RealtimeSessionConfig` because MEAI's `RealtimeSessionOptions` has no slot for the last two and the session converts the rest internally. |
| `RealtimeAudioConfig` | Local type + MEAI `RealtimeAudioFormat` | We keep `RealtimeAudioConfig` for Gemini-specific defaults (16k in / 24k out / 16-bit / mono). MEAI's `RealtimeAudioFormat(mediaType, sampleRate)` is constructed from it when needed. |
| `RealtimeInput.Text` / `RealtimeInput.Audio` | Internally → `CreateConversationItemRealtimeClientMessage` (text) / `InputAudioBufferAppendRealtimeClientMessage` (audio) | `VoiceSession.SendAsync(RealtimeInput)` stays as the consumer-facing API. Inside, we translate to MEAI client-message subtypes before handing to `IRealtimeClientSession.SendAsync`. |
| `ToolResponse` | Internally → `CreateConversationItemRealtimeClientMessage` with a `FunctionResultContent` payload | Same pattern — public surface preserved, internal translation. |

## Server event map

`RealtimeServerEvent` stays as the consumer-facing discriminated union. Inside
`VoiceSession`, the read loop pulls MEAI `RealtimeServerMessage` values from
`IRealtimeClientSession.GetStreamingResponseAsync` and translates them to
`RealtimeServerEvent` subtypes via `MeaiToSutandoEventAdapter`.

| Sutando event | MEAI message | Notes |
|---|---|---|
| `RealtimeSetupComplete` | `RealtimeServerMessage { Type = "SessionStarted" }` (Sutando-defined custom type — see `SutandoRealtimeMessageTypes.SessionStarted`) | MEAI has no first-class "session ready" message. Gemini emits `setupComplete` distinctly from "response created", so we synthesise. |
| `RealtimeAudioOutput` | `OutputTextAudioRealtimeServerMessage { Type = OutputAudioDelta, Audio = base64 }` | `RealtimeAudioOutput.Pcm` (raw bytes) ↔ base64 string. Base64 decode at the boundary. |
| `RealtimeInputTranscription` | `OutputTextAudioRealtimeServerMessage { Type = InputAudioTranscriptionDelta / Completed }` | MEAI splits delta vs completed; we collapse to the existing `Finished` flag. |
| `RealtimeOutputTranscription` | `OutputTextAudioRealtimeServerMessage { Type = OutputAudioTranscriptionDelta / Done }` | Same pattern. |
| `RealtimeInterrupted` | `ResponseCreatedRealtimeServerMessage { Type = ResponseDone, Status = Cancelled }` | Per MEAI docs: barge-in surfaces as a cancelled response. |
| `RealtimeTurnComplete` | `ResponseCreatedRealtimeServerMessage { Type = ResponseDone, Status = Completed }` | |
| `RealtimeToolCall` | `ResponseOutputItemRealtimeServerMessage { Type = ResponseOutputItemDone, Item.Contents = [FunctionCallContent] }` | Matches the contract `FunctionInvokingRealtimeClientSession` middleware expects. |
| `RealtimeToolCallCancellation` | `RealtimeServerMessage { Type = SutandoRealtimeMessageTypes.ToolCallCancelled }` | No MEAI equivalent (the OpenAI realtime protocol cancels via response cancellation, not a separate "these tool calls are rescinded" envelope). Sutando-specific. |
| `RealtimeGroundingMetadata` | `RealtimeServerMessage { Type = SutandoRealtimeMessageTypes.GroundingMetadata, RawRepresentation = JsonElement }` | Gemini-specific surface; no MEAI peer. |
| `RealtimeGoAway` | `RealtimeServerMessage { Type = SutandoRealtimeMessageTypes.GoAway, RawRepresentation = GoAwayBody }` | Gemini-specific reconnect/back-off semantics. |
| `RealtimeSessionResumptionUpdate` | `RealtimeServerMessage { Type = SutandoRealtimeMessageTypes.SessionResumptionUpdate, RawRepresentation = ResumptionBody }` | Gemini-specific resumption-handle stream. The closest MEAI peer is `RealtimeSessionOptions.RawRepresentationFactory`, but that's an input not an event. |
| `RealtimeTransportClosed` | `RealtimeServerMessage { Type = SutandoRealtimeMessageTypes.SessionClosed, RawRepresentation = CloseInitiator }` | Local-only event, no MEAI peer. We emit it as the final message before completing the stream. |
| `RealtimeTransportError` | `ErrorRealtimeServerMessage` (`Type = Error`) | Closest MEAI peer — carries a string-shaped error blob. |

## Deviations from MEAI's intended contract

1. **`SetupComplete` is Sutando-specific.** Gemini Live emits a dedicated
   "setup acknowledged" frame distinct from "the model started responding".
   MEAI's protocol assumes OpenAI-style "session created + ready for input
   in one step". We surface `setupComplete` as a custom message type so
   downstream state machines (`VoiceSession`'s `Connecting → Listening`
   transition) keep working.

2. **`GoAway` + `SessionResumptionUpdate` have no MEAI peer.** These are
   Gemini-specific reconnect/resumption signals. We keep them as Sutando-
   custom `RealtimeServerMessageType` values; their payloads live on
   `RawRepresentation`. Any future MEAI realtime client that wants the same
   semantics will need analogous custom types.

3. **No `FunctionInvokingRealtimeClient` middleware.** MEAI ships a delegating
   middleware that auto-invokes tool calls coming over the realtime stream.
   We deliberately do not use it: `VoiceSession.DispatchToolAsync` keeps the
   manual orchestration (registered tool handlers, error-response synthesis,
   background `Task.Run`) the existing tests depend on. The middleware is
   strictly more powerful but slightly more opinionated; switching to it
   would be a follow-up.

4. **`RealtimeServerEvent` is consumer-facing; MEAI types are internal.**
   `Sutando.Voice.VoiceWebSocketHandler.MapEvent` and
   `Sutando.Phone.TwilioMediaSocketHandler.ForwardEventAsync` switch on the
   sealed `RealtimeServerEvent` hierarchy. Forcing them onto MEAI's
   open-string `RealtimeServerMessageType` would be churn for no benefit.
   The MEAI types stop at `VoiceSession`'s read loop, which translates to
   `RealtimeServerEvent` before raising `EventReceived`.

5. **`VoiceSession` ctor takes an `IRealtimeClient`, not a factory.** MEAI
   clients are reusable across sessions; only the *session* is one-shot.
   `VoiceSession` calls `CreateSessionAsync` on each connect (including
   reconnects), preserving the bodhi-port reconnect path that allocates a
   fresh underlying socket per attempt.

## What still smells

- The `RawRepresentation` slot on `RealtimeServerMessage` is the obvious
  escape hatch for Gemini-specific event payloads, but it's typed as `object?`.
  Future providers that want to share this code path would need a discriminator
  to decode correctly. For now, only Sutando's own adapter writes/reads it,
  so this is fine.
- `Sutando.Realtime` now hard-references MEAI experimental types. Project-
  level `<NoWarn>$(NoWarn);MEAI001</NoWarn>` documents that we accept the
  churn risk; downstream consumers (Voice, Phone, tests) inherit nothing.
