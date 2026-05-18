# Voice WS wire protocol (Sutando.Voice)

This document describes the JSON envelope the browser exchanges with `Sutando.Voice` over the
`/voice` WebSocket endpoint (default port `9900`). The envelope is **deliberately divergent**
from upstream sutando — see `INTEGRATION-NOTES.md` for the rationale.

Both directions use WebSocket **text frames** carrying UTF-8 JSON. Property names are snake_case
(`{"time_left_ms": 1000}`), enum values are lower-case strings. The server is tolerant of
case-insensitive client property names so a TypeScript client that ships PascalCase fields still
parses.

## Client → server

| `type`         | Other fields            | Notes |
| -------------- | ----------------------- | ----- |
| `audio`        | `data` (base64 string)  | Base64-encoded PCM 16 kHz mono 16-bit. Forwarded to `IRealtimeTransport.SendRealtimeInputAsync` as `RealtimeInput.Audio(...)`. |
| `text`         | `text` (string)         | A user text turn (no audio). Sent to the model as `RealtimeInput.Text(...)`. The legacy field name `data` is also accepted as a synonym for `text`. |
| `interrupt`    | —                       | User barge-in. **Logged only** in this slice — `IRealtimeTransport` is fixed and exposes no interrupt API. See deferred work. |
| `end_turn`     | —                       | Explicit turn-complete hint from the client. **Logged only**. |

Examples:

```json
{ "type": "audio", "data": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=" }
{ "type": "text", "text": "what time is it?" }
{ "type": "interrupt" }
{ "type": "end_turn" }
```

## Server → client

| `type`                 | Other fields                                  | Source `RealtimeServerEvent`           |
| ---------------------- | --------------------------------------------- | -------------------------------------- |
| `setup_complete`       | —                                             | `RealtimeSetupComplete`                |
| `audio`                | `data` (base64 string)                        | `RealtimeAudioOutput` (24 kHz mono 16-bit PCM) |
| `input_transcription`  | `text` (string)                               | `RealtimeInputTranscription`           |
| `output_transcription` | `text` (string)                               | `RealtimeOutputTranscription`          |
| `interrupted`          | —                                             | `RealtimeInterrupted`                  |
| `turn_complete`        | —                                             | `RealtimeTurnComplete`                 |
| `go_away`              | `time_left_ms` (int, optional), `message` (optional) | `RealtimeGoAway`                |
| `error`                | `message` (string)                            | `RealtimeTransportError`, plus startup errors (e.g. missing API key) |

Transcription envelopes are emitted **per fragment** — the `Finished` flag on
`RealtimeInputTranscription` / `RealtimeOutputTranscription` is not exposed on the wire because
the spec envelope has no slot for it. Clients concatenate fragments until a `turn_complete` or
`interrupted` arrives.

Events not surfaced to the browser (handled server-side or future work):

- `RealtimeToolCall`, `RealtimeToolCallCancellation` — tool dispatch runs inside `VoiceSession`.
- `RealtimeGroundingMetadata` — operator concern; emit in a follow-up if needed.
- `RealtimeSessionResumptionUpdate` — cached on the server-side session, used on reconnect.
- `RealtimeTransportClosed` — surfaced via the WebSocket close frame.

## Close semantics

When the underlying Gemini transport closes (server-initiated, network drop, or operator shutdown)
the WebSocket is closed with a normal-closure status. The browser is expected to reconnect by
opening a new socket — there is no server-side replay of conversation items in this slice.

A missing `GEMINI_VOICE_API_KEY` / `GEMINI_API_KEY` on the server closes each `/voice` upgrade with
WS status `1008` (policy violation) and a preceding `{"type":"error","message":"…"}` text frame so
the client gets a structured signal instead of an opaque drop.
