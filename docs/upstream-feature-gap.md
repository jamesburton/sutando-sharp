# Upstream feature gap — sutando vs sutando-sharp

Snapshot taken 2026-05-25. Upstream tracked at `..\ThirdParty\sutando\`. Compares
each capability in upstream's README "What's inside" table + the 27-skill
catalog (`skills/MANIFEST.md`) against the current `main` of sutando-sharp,
including the three parallel agents currently running (Wave C: Gmail + Calendar;
Wave D: make-viral-video; CLI wiring for `SkillRegistry`).

Companion to [`skills-taxonomy.md`](skills-taxonomy.md), which classifies the
27 skills by porting strategy. This doc is the *result* — what those decisions
have left us with so far.

## Legend

- ✓ **Done** — sutando-sharp ships an implementation with broadly upstream-equivalent behaviour.
- ◐ **Partial** — exists but with reduced surface area or missing actions; the bones are in place to extend.
- → **Coming this wave** — one of the three in-flight agents lands this; status will flip to ✓ when they merge.
- ❌ **Missing** — nothing ported yet; would be a fresh implementation pass.
- 🚫 **Deferred by design** — `[C]` Mac-only or `[B]` cross-OS items intentionally not in scope (see [`skills-taxonomy.md`](skills-taxonomy.md)).

## Core infrastructure (the spine — voice / phone / channels / executor)

| Capability | Upstream | sutando-sharp | Notes |
|---|---|---|---|
| Voice conversation (Gemini Live, browser WS :9900) | `voice-agent.ts` | ✓ `Sutando.Voice` + `Sutando.Realtime` | Browser-side wire protocol re-implemented; see `docs/voice-wire-protocol.md`. |
| Local-stack offline voice (Whisper + Kokoro + LlamaSharp + Silero) | (not in upstream) | ✓ `Sutando.Voice.Local` + `Sutando.LocalInference.*` | Net-new vs upstream; `sutando voice --local` mode. |
| Phone bridge (Twilio Media Streams ↔ Gemini Live) | `phone-conversation/` | ✓ `Sutando.Phone` | Inbound + outbound calls. |
| Task delegation file-bridge (`tasks/` → `results/`) | `task-bridge.ts` | ✓ `Sutando.Bridge` | Marker grammar + archive + file watcher. |
| Pluggable agent executor (Claude CLI / Anthropic HTTP) | Claude Code CLI hard-wired | ✓ `Sutando.Core.IAgentExecutor` | `Sutando.Core` provides both Claude-CLI shell-out and Anthropic-HTTP direct paths under one interface — strictly broader than upstream. |
| Cross-device task submission API on :7843 | `agent-api.py` | ✓ `Sutando.Api` | Bearer-auth, same port. |
| Read-only system dashboard on :7844 | `dashboard.py` | ✓ `Sutando.Dashboard` | SignalR live push for status. |
| Telegram messaging | `telegram-bridge.py` | ✓ `Sutando.Channels.Telegram` | Tokens via env. |
| Discord messaging (text) | `discord-bridge.py` | ✓ `Sutando.Channels.Discord` | DM + channel + tier sandboxing. |
| Browser automation | `browser.mjs` + MCP tools | ✓ `Sutando.Browser` | Playwright wrapper matching upstream action grammar. |
| Skills runtime | Manifest-loaded TS tools | ✓ `Sutando.Skills` | Plus on-disk script runtimes (Python/Node/Bash/dotnet-tool). |
| Menu-bar app + global hotkeys | `src/Sutando/main.swift` (Cocoa, macOS-only) | ◐ `Sutando.Platform.Windows` | Win32 `RegisterHotKey` plumbed; no tray UI; no avatar states; no "Open Core" / "Open Dashboard" menu entries. |

## Cloud integrations (#13 — the wave currently shipping)

| Skill | Upstream | sutando-sharp | Notes |
|---|---|---|---|
| `gemini-tts` | A bucket | ✓ `GeminiTextToSpeechSkill` | 24 kHz mono WAV out to `artifacts/`. Live API smoke test in tests. |
| `openai-tts` | A bucket | ✓ `OpenAiTextToSpeechSkill` | mp3 default; format configurable (opus / aac / flac / wav / pcm). |
| `image-generation` | A bucket | ✓ `GeminiImageGenerationSkill` | Image-modality Gemini; mime → ext; text rationale into `Body`. Veo / video generation deliberately deferred. |
| `x-twitter` | A bucket | ✓ `XTwitterSkill` | OAuth1 over v2 `/2/tweets`; new `OAuth1Signer` helper. |
| `gmail` | A bucket (new shape) | → Wave C agent | Targeting `search` + `get` actions for phase 1; `send` / `draft` deferred. |
| `calendar` (Google) | A bucket (new shape) | → Wave C agent | Targeting `upcoming` + `create` actions for phase 1; `delete` / `update` deferred. |
| `make-viral-video` | A bucket (orchestration) | → Wave D agent | Minimal slideshow port — generate N images + ffmpeg concat. No music / captions / transitions; those are follow-ups (see Wave D scope decision in the commit). |

## Skill-runtime gaps (capabilities not yet ported)

Numbers in `[…]` are the porting buckets from [`skills-taxonomy.md`](skills-taxonomy.md).

| Upstream skill | Bucket | Why it's not here yet | Path to add |
|---|---|---|---|
| `proactive-loop` | [A] | The autonomous build loop — runs every 5 min, drives every other skill. **Highest-impact missing piece** — nothing autonomous lights up without it. | A `Sutando.Workspace`-aware long-running host service that polls `tasks/`, drives the executor, and fires scheduled skills. Could live under `Sutando.Core` or a new `Sutando.Proactive` project. |
| `schedule-crons` | [B] | Cron driver. Required before any periodic skill (`info-radar` daily digest, health check, archive-stale-results) is useful. | Quartz.NET or a simple in-process scheduler. Reads `crons.json` from the workspace. |
| `info-radar` | [A] | arXiv / GitHub / HN / news monitoring with a daily digest. | Another `ISkill` under `Sutando.Skills.Cloud` (no auth — HTTP-only); needs `schedule-crons` to run on a cadence. |
| `claude-codex` / `claude-gemini` / `claude-router` | [A] | `Sutando.Core` has the Claude CLI executor; the routing logic + Gemini-CLI executor are not ported. | Add a `GeminiCliExecutor : IAgentExecutor` (mirror the existing Claude CLI shell-out) and a small router that picks between them per task. |
| `meeting-prep` | [A] | Orchestrates gmail + calendar + contacts. | After Wave C lands, this is gmail + calendar present; **contacts is the missing primitive** ([C] today — needs a Microsoft Graph / Google People SDK adapter to be usable on Windows). |
| `morning-briefing` | [A] | Orchestrates gmail + calendar + Discord + health-check. | Same dependency story as meeting-prep; also needs `health-check` (still missing). |
| `subscription-scanner` | [A] | Gmail-search + JSON-state diffing. | Trivial after Wave C — sits on top of `gmail` skill's `search` action. |
| `regression-search` | [A] | JSONL parsing + keyword heuristics. | Pure logic port. Low complexity. |
| `self-diagnose` | [A] | Reads logs/git/memory/build_log; cross-node SSH mode. | Pure managed code + SSH.NET for the cross-node mode. |
| `deal-finder` | [A] | Craigslist scrape + Twilio SMS + Telegram drop. | Sutando.Browser (or HtmlAgilityPack) + Twilio adapter (new) + existing Telegram channel. |
| `call-diagnostics` | [A] | Reads `calls.jsonl` + heuristic detectors → HTML report. | Pure data port. Low complexity. |
| `bot2bot-post` | [A] | Discord REST cross-instance messaging. | Already have `Sutando.Channels.Discord`; this is a thin skill on top of its REST surface. |
| `health-check` | [A] | Process / port / log probes; OS-supervised launchd watchdog on macOS. | Cross-platform port reasonable; the launchd wrapper becomes a Windows Service or scheduled task. |

## Architectural gaps (entire upstream concepts not yet present)

These aren't "one missing skill" — they're cross-cutting capabilities upstream
treats as core that sutando-sharp doesn't yet model.

1. **Autonomous proactive build loop.** Upstream's defining feature: "Most of Sutando's code was written this way." Drives `info-radar` digests, `archive-stale-results`, health checks, autonomous improvement work. Without it, sutando-sharp is purely reactive. **Single biggest gap.**
2. **Notes / second brain.** Upstream stores YAML-frontmatter markdown notes the agent can search and act on. Sutando-sharp's `Sutando.Workspace` has `notes/` as a directory but no managed search / write / tag layer.
3. **Cross-node memory + notes sync (`cross-node-sync` skill).** Private-git-repo-backed memory sync across machines. Important for the "fleet of Mac minis / MacBook" use case upstream demos. Cross-platform port is feasible via SSH.NET / WinSCP.
4. **3-tier access enforcement at every channel.** Upstream gates every transport (phone / Discord / Telegram / web) by owner / verified / unverified bands. `Sutando.Channels.Discord` ships the tier model; the other channels and the voice/phone surfaces need to converge on the same `ITierResolver` abstraction.
5. **STIR/SHAKEN inbound-call verification.** Upstream's phone-conversation skill does carrier-attestation checks before owner access is granted. `Sutando.Phone` does not yet model attestation level — it would slot into the `PhoneTierResolver` already there.
6. **Manifest-tool injection into the voice agent's runtime tool table.** Upstream's `loadSkillManifestTools()` dynamically imports per-skill `tools.ts` files and merges them into the voice + phone tool table at startup. Sutando-sharp's `Sutando.Realtime` voice session has a tool-dispatch path but no equivalent dynamic-skill-import bridge. Once the CLI wiring agent lands, the registry is reachable from the CLI; the next step would be wiring the same registry into `VoiceSession`'s tool surface so skills are invokable from voice.
7. **Quota-tracker / credential vault.** Upstream uses Mac Keychain; sutando-sharp uses env vars (phase 1 of #13). For long-term use, a credential vault layer (DPAPI on Windows, libsecret on Linux, Keychain on macOS) lives behind the env-var fallback. Already noted as deferred in `cloud-integrations-scope.md`.

## Platform gaps (the [C] bucket)

These are deliberately deferred until a macOS or Linux node comes online:

🚫 `macos-tools` — AppleScript + `mdfind` + Mail.app + Calendar.app + Contacts.app + Reminders. Windows replacements would be Microsoft Graph (mail/calendar/contacts) + Toast notifications (reminders) + Win32 file-search APIs — a separate skill family entirely. Wave C's `gmail` + `calendar` are a partial start on the Graph-equivalent surface.

🚫 `gws-gmail-voice` — Mac-only `gws` CLI wrapper. Replaced architecturally by Wave C's `gmail` skill (direct Gmail API). No further Mac port needed; existing Windows path covers it.

🚫 `screen-record` — `screencapture -v` + `ffmpeg avfoundation` + osascript + Mac menu-bar indicator. Mac-only stack. Windows path would use ffmpeg `gdigrab` + tray UI — separate implementation, not a port.

🚫 `discord-voice` — discord.js voice channel + Gemini Live + macOS CGEvent clicks for screen-share. Voice-channel half is cross-platform via DSharpPlus.VoiceNext (or similar); screen-share half is Mac-specific. Could ship voice-only on Windows as a useful subset.

🚫 `macos-use` — Accessibility-tree GUI automation. Windows equivalent is UIAutomation (FlaUI). Same contract, different impl. Not yet built.

🚫 `screen-companion` — voice + vision via Gemini Live + Mac ScreenCaptureKit. Voice + vision halves are cross-platform; Windows screen-capture cadence would use Windows Graphics Capture API. Foundational pieces exist (`Sutando.Voice` + `Sutando.Platform.Windows` capture); the companion-mode wiring does not.

🚫 `cross-node-sync` — rsync-over-ssh on macOS. Windows equivalent is SSH.NET / WinSCP / Syncthing. Architectural gap rather than impossible.

## Summary by category

After the three in-flight agents merge, sutando-sharp will be at roughly:

- **Core spine:** ≈90% parity with upstream — voice, phone, channels, executor, browser, bridge, API, dashboard, local-stack are all on par or richer than upstream.
- **Cloud skills:** ≈100% of the [A]-bucket A/V + Twitter + Google Workspace subset, after Wave C/D land.
- **Autonomous behaviour:** 0%. No `proactive-loop`, no `schedule-crons`, no `health-check`. This is the next major architectural wave after #13 closes.
- **Mac-specific surface ([B]/[C] buckets):** intentionally deferred — at least 8 skills won't have direct ports until macOS support lands.
- **Notes / second brain:** missing entirely. Likely a small focused phase of its own.

**Highest-leverage next phases** (after #13 closes):

1. **Autonomous loop foundation** — `proactive-loop` + `schedule-crons` together; everything else periodic depends on these. Probably a new `Sutando.Proactive` project.
2. **Notes service** — a thin `Sutando.Notes` library on top of `Sutando.Workspace` that exposes managed search + write + tag operations, plus a manifest of skill tools so the agent can use it from voice.
3. **Voice-tool bridge** — wire `SkillRegistry` (after the CLI wiring agent lands) into `VoiceSession`'s tool-dispatch path so cloud skills become invokable from a live voice call. This is the unlock that turns the cloud skills from "callable from `sutando skills run`" into "ask Sutando to tweet for me."
4. **Cross-node sync** — once there's two machines worth syncing between, a private-git-repo-backed memory sync.

Out of the upstream catalog's 27 skills, after the in-flight wave:
- ✓ Done: 4 + 3 (cloud) = 7
- ◐ Partial: ≈4 (gmail/calendar phase 1, viral-video minimal, claude-router subset)
- ❌ Missing but portable (A-bucket): ≈8
- 🚫 Deferred by design ([B]/[C]): ≈8
