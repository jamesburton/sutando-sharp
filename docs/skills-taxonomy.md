# Sutando Skills Port Taxonomy (macOS/TS+Py → .NET 10, Windows-first)

Buckets:

- **A) Genuine port** — pure logic / HTTP / file I/O. Rewrite as managed .NET; runs identically on Win/Mac/Linux.
- **B) Reimplement against host OS** — same contract, different per-OS APIs.
- **C) Stub on Windows, real impl when macOS comes online** — fundamentally Mac-only (AppleScript, `screencapture`, `gws` CLI, etc.).

## Classifications

- **[A] bot2bot-post** — Discord REST post to `#bot2bot` reading `.env`/JSON config; pure HTTP + file I/O.
- **[A] call-diagnostics** — Reads `calls.jsonl` + `call-metrics.jsonl`, heuristic detectors, renders HTML report; no OS APIs.
- **[A] claude-codex** — Shells out to `codex` CLI which is already cross-platform; only bash wrapper needs porting to `Process.Start`.
- **[A] claude-gemini** — Same shape as claude-codex; `gemini` CLI runs on Windows; wrap with managed process invocation.
- **[A] claude-router** — Pure routing logic over claude-codex / claude-gemini; trivial port.
- **[B] cross-node-sync** — rsync-over-ssh today; real shape is "trusted-host file sync." Reimplement over SSH.NET / WinSCP / Syncthing; same contract.
- **[A] deal-finder** — HTTP fetch + HTML parse (Craigslist), Twilio SMS, Telegram via file drop, JSON state; cross-platform.
- **[B] discord-voice** — discord.js + Gemini Live (cross-platform) but screen-share branch uses CGEvent clicks; B with the screen-share branch initially stubbed.
- **[A] gemini-tts** — HTTP POST to Gemini TTS, write mp3 to disk; nothing OS-specific.
- **[C] gws-gmail-voice** — Sole purpose is wrapping the macOS-installed `gws` CLI; future Windows port would use Google Workspace SDK directly (different skill).
- **[A] image-generation** — HTTP to Gemini Flash Image / Veo; image bytes to disk. Pure API.
- **[A] info-radar** — Web search + fetch over arXiv/GitHub/HN; pure HTTP + JSON state.
- **[C] macos-tools** — AppleScript + `mdfind` + Mail.app + Calendar.app + Contacts.app. Seven sub-tools all Mac-only; defer en bloc until macOS node comes online (Windows equivalents are Graph API / WinRT, a separate Windows-native skill family).
- **[B] macos-use** — Contract is "GUI automation via accessibility tree." macOS uses AX API; Windows equivalent is UIAutomation (FlaUI). Same contract, different impl.
- **[A] make-viral-video** — Codex CLI + ffmpeg + PIL + Gemini/OpenAI HTTP; all cross-platform tools. PIL → ImageSharp/SkiaSharp.
- **[A] meeting-prep** — Orchestrates google-calendar + contacts + gmail lookups. Pure orchestration. (Note: depends on macos-tools today for contacts — needs Graph-style Windows backend to fully work.)
- **[A] morning-briefing** — Orchestration over gws/calendar/Discord/health-check; pure logic. (Same B/C-dependency caveat as meeting-prep.)
- **[A] openai-tts** — HTTP POST to OpenAI TTS, write mp3; pure API.
- **[A] phone-conversation** — Twilio Media Streams WebSocket + Gemini Live; both cross-platform SDKs.
- **[A] proactive-loop** — File-watching + JSON state + orchestration. The `Monitor` tool / `fswatch` step becomes `FileSystemWatcher`.
- **[B] quota-tracker** — HTTP proxy + header parsing is portable, but credential read uses macOS Keychain → needs DPAPI / Credential Manager on Windows.
- **[A] regression-search** — Pure JSONL parsing + keyword heuristics. Trivial port.
- **[B] schedule-crons** — Reads `crons.json` and registers jobs. Cron on macOS → Task Scheduler / Quartz.NET / in-process scheduler on Windows. Same contract.
- **[B] screen-companion** — Voice + vision via Gemini Live (cross-platform); screen-capture cadence is per-OS (Mac ScreenCaptureKit vs Win Graphics Capture API).
- **[C] screen-record** — `screencapture -v` + `ffmpeg avfoundation` + osascript notifications + menu-bar indicator. Mac-only stack; Windows path would use ffmpeg `gdigrab` + toast notifications + tray icon — that's a fresh implementation, defer.
- **[A] self-diagnose** — Reads logs/git/memory/build_log; SSH for cross-node mode (use SSH.NET). All managed code.
- **[A] subscription-scanner** — Gmail-search prompt template + JSON state diffing. Pure data orchestration via the Gmail/MCP layer.
- **[A] x-twitter** — X/Twitter API v2 over HTTP with OAuth1; pure API.

## Summary

### Counts

- **A (genuine port):** 18 — bot2bot-post, call-diagnostics, claude-codex, claude-gemini, claude-router, deal-finder, gemini-tts, image-generation, info-radar, make-viral-video, meeting-prep, morning-briefing, openai-tts, phone-conversation, proactive-loop, regression-search, self-diagnose, subscription-scanner, x-twitter.
- **B (reimplement per OS):** 6 — cross-node-sync, discord-voice (screen-share branch only), macos-use, quota-tracker, schedule-crons, screen-companion.
- **C (stub on Windows, defer):** 3 — gws-gmail-voice, macos-tools, screen-record.

### Top 3 to port FIRST (foundational, not most-used)

1. **schedule-crons (B)** — every recurring skill depends on it; no automation lights up without scheduling. Map to Quartz.NET or Task Scheduler.
2. **proactive-loop (A)** — orchestration core; nothing autonomous exists without it. Drives every other skill.
3. **claude-router + claude-codex + claude-gemini (A)** — AI-delegation backbone the loop uses every pass; trivial `Process.Start` ports.

(phone-conversation is the killer-feature port — schedule after the loop exists.)

### Top 3 to DEFER

1. **cross-node-sync (B)** — explicitly optional; Mac-to-Mac scoped; no Windows node to sync to yet.
2. **screen-record (C)** — niche; large native surface; Windows users can use OBS / Win+G in the interim.
3. **gws-gmail-voice (C)** — voice-only Gmail triage path; until phone/voice is wired on Windows it has no consumer, and the real Windows replacement is "call Graph/Gmail SDK directly" — a different skill.

### Porting-order caveat

Several A-bucket skills are pure orchestration over B/C primitives — **meeting-prep** and **morning-briefing** call macos-tools (C) for contacts/email and **macos-use** (B) for GUI work. They'll port cleanly as code but won't be functionally useful until Windows equivalents (Microsoft Graph for mail/calendar/contacts, UIAutomation for GUI) land. Sequence: B-primitives → A-orchestrators that depend on them.
