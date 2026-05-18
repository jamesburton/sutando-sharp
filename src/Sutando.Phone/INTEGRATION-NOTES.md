# Sutando.Phone — Integration Notes

This document tracks how `Sutando.Phone` plugs into the rest of the solution and what the
follow-up integration phase has to wire up. It exists so the next contributor can pick
up the file without re-deriving the rationale from commit history.

## Solution wiring

`Sutando.sln` is **deliberately unmodified** in this slice — per the task brief. The new
project is pulled into `dotnet build Sutando.sln` transitively through
`tests/Sutando.Tests/Sutando.Tests.csproj`, which now includes:

```xml
<ProjectReference Include="..\..\src\Sutando.Phone\Sutando.Phone.csproj" />
```

When the follow-up integration phase wires `Sutando.Phone` into the CLI dispatcher and
declares the `sutando phone` verb, add the project to `Sutando.sln` directly at that
point with:

```pwsh
dotnet sln Sutando.sln add src/Sutando.Phone/Sutando.Phone.csproj
```

Until then, the transitive path keeps both build and test on the happy path.

## CLI verb wiring (deferred)

Mirror the existing `sutando voice` verb. In `src/Sutando.Cli/Commands.cs`, add a `Phone`
case to the switch that runs:

```csharp
case "phone":
    return await Sutando.Phone.PhoneCommand.RunAsync(remainingArgs, ct).ConfigureAwait(false);
```

And add `Sutando.Phone` to the `Sutando.Cli.csproj`:

```xml
<ProjectReference Include="..\Sutando.Phone\Sutando.Phone.csproj" />
```

The `docs/cli.md` Servers table should grow a row:

| Verb | Port | What |
|---|---|---|
| `sutando phone` | 3100 | Twilio phone bridge. `/twilio/incoming` answers calls (TwiML → Media Streams); `/twilio/media` is the per-call audio WebSocket; `/twilio/outbound` initiates outbound calls (bearer-gated); `/healthz` reports active call count. |

## README env-var snippet (deferred)

The top-level `README.md` "Configuration" table should grow:

| Env var | Used by |
|---|---|
| `SUTANDO_PHONE_PORT` | `sutando phone` bind port (default `3100`) |
| `TWILIO_ACCOUNT_SID` + `TWILIO_AUTH_TOKEN` + `TWILIO_PHONE_NUMBER` | `sutando phone` — Twilio identity for outbound calls + inbound webhook signature validation |
| `OWNER_NUMBER` (comma-separated) | `sutando phone` — owner caller-id allow-list |
| `VERIFIED_CALLERS` (comma-separated) | `sutando phone` — verified caller-id allow-list |
| `SUTANDO_PHONE_OUTBOUND_TOKEN` | `sutando phone` — bearer token gating `POST /twilio/outbound` |
| `SUTANDO_PHONE_PUBLIC_HOST` | `sutando phone` — public hostname for the Media Streams WSS URL (typically an ngrok tunnel) |
| `SUTANDO_PHONE_ALLOW_UNSIGNED` | `sutando phone` — DEV-ONLY bypass for Twilio signature validation |

## STIR/SHAKEN attestation — header vs body

**The task brief described `StirVerstat` as a set of HTTP headers (`TN-Validation-Passed-A`,
`TN-Validation-Passed-B`, etc.). Twilio's actual contract is the opposite: `StirVerstat` is a
form parameter on the webhook POST body**, not an HTTP header. Verified against the upstream
`sutando` reference implementation at `skills/phone-conversation/scripts/conversation-server.ts`
which reads it as `form.get('StirVerstat')`.

The values are still the documented `TN-Validation-Passed-A` / `TN-Validation-Passed-B` /
`TN-Validation-Failed` / `No-TN-Validation` strings — only the carrier mechanism differs.

Upstream policy (preserved): `TN-Validation-Passed-A` is the cryptographically-trusted
result; anything else downgrades an OWNER-matched call to `Verified` and logs the downgrade
event. The `tierDowngraded` flag on the Stream `<Parameter>` carries this forward to the
Media Streams handler.

## μ-law / resampler choices

Hand-rolled — NAudio was considered and rejected as a dependency. The codec is a 256-entry
decode table plus a 256-entry segment lookup; pulling NAudio just to consume two tables
would be more dependency surface than the whole rest of the phone bridge.

**Table values match NAudio's `MuLawDecompressTable` / `MuLawCompressTable` verbatim** —
which is itself the canonical ITU-T G.711 reference. We deliberately diverge from the upstream
TypeScript reference `conversation-server.ts` (`pcmToMulaw` + `mulawTopcm16k`) here: their
decode formula `((mantissa << 1) + 33) * (1 << exponent) - 33` collapses the dynamic range to
±8031, which is correct-with-itself but loses ~4× compared to standard G.711 (±32124). On the
wire μ-law is endian-neutral, so the upgrade is transparent to Twilio at the carrier end;
audio quality at the model end is better.

The resampler is naive linear interpolation:
- **8 kHz → 16 kHz (inbound)**: linear midpoint between adjacent input samples.
- **24 kHz → 8 kHz (outbound)**: 3-sample averaging window.

Both directions have measurable alias artefacts above ~3.5 kHz. The carrier's 4 kHz Nyquist
filter masks the outbound aliasing in practice. A v2 polyphase replacement (NAudio's
`WdlResamplingSampleProvider` or libsamplerate via P/Invoke) is the natural follow-up.
Tracked in `AudioResampler.cs` xmldoc.

## Twilio dependency surface

Just `Twilio` 7.14.9. **Twilio.AspNet.Core was rejected**:
- It targets net7/8/9 (no net10 yet — would load, but is technically out-of-band).
- It pulls a FrameworkReference on `Microsoft.AspNetCore.App` for plumbing we already have.
- We use exactly one piece of it: `RequestValidationHelper.IsValidRequest`. That helper is
  itself a 20-line wrapper around `Twilio.Security.RequestValidator`, which lives in the
  base `Twilio` package. We call `RequestValidator.Validate(url, params, expected)`
  directly from `PhoneServer.IsValidTwilioRequestAsync`.

This keeps the dependency surface flat — same approach Sutando.Realtime took with
`Google_GenerativeAI.Live` (one targeted SDK, no peripheral wrappers).

## Outbound endpoint authentication

The task brief didn't specify auth for `POST /twilio/outbound`, but that endpoint can place
calls on the owner's Twilio bill. Shipping it open would be a security bug.

Resolved by gating it behind a phone-specific bearer token,
`SUTANDO_PHONE_OUTBOUND_TOKEN`. When unset, the endpoint returns 503 ("not configured").
When set, requests must carry `Authorization: Bearer <token>` or get a 401.

The choice of a phone-specific token (rather than reusing `SUTANDO_API_TOKEN` from
Sutando.Api) is deliberate: the phone bridge and the API have different threat surfaces and
the operator may want to rotate them independently.

## Call metadata

Per the brief, each call writes a record to `<workspace>/data/phone/<call-sid>.json`. Format:

```json
{
  "call_sid": "CA...",
  "from": "+1...",
  "to": "+1...",
  "direction": "inbound",
  "tier": "owner",
  "stir_attestation": "TN-Validation-Passed-A",
  "tier_downgraded": false,
  "started_at": "2026-05-18T...",
  "ended_at": "2026-05-18T...",
  "duration_ms": 47200,
  "tool_calls": [
    { "name": "work", "at": "2026-05-18T...", "arguments_preview": "..." }
  ]
}
```

`CallMetadataStore` performs atomic writes via temp-file + rename. The dashboard SignalR
hub (`Sutando.Dashboard.StatusHub`) does not yet subscribe to these files — adding a
`phone_call_added` / `phone_call_ended` broadcast is a deferred follow-up (parallels the
existing `task_added` / `result_added` events).

## Constraints honoured

- `Sutando.sln` is unchanged.
- `Sutando.Cli` is unchanged.
- Top-level `README.md` is unchanged (snippet drafted above for the integration phase).
- All tests pass on both `net10.0` and `net10.0-windows10.0.19041.0`.
- No new files outside `src/Sutando.Phone/` and `tests/Sutando.Tests/Phone/`.
