# Cloud Integrations Scope (#13 — P9)

Status: scoping. This document is the decomposition for #13 — porting the
upstream Sutando project's "A-bucket" cloud integrations (pure HTTP / API
skills) to the .NET port.

## Goals

Land six skills against the existing `Sutando.Skills` runtime as
`SkillRuntime.Managed` implementations:

| ID                | Upstream skill         | Auth                                                                                       | Complexity   |
|-------------------|------------------------|--------------------------------------------------------------------------------------------|--------------|
| `gemini-tts`      | gemini-tts             | `GEMINI_API_KEY` (reused from voice)                                                       | low          |
| `openai-tts`      | openai-tts             | `OPENAI_API_KEY` (new)                                                                     | low          |
| `image-generation`| image-generation       | `GEMINI_API_KEY` (Gemini Flash Image / Veo)                                                | low–medium   |
| `x-twitter`       | x-twitter              | `TWITTER_API_KEY` + `_API_SECRET` + `_ACCESS_TOKEN` + `_ACCESS_SECRET` (OAuth1 over v2)    | medium       |
| `gmail`           | (new shape; see notes) | `GOOGLE_OAUTH_CLIENT_ID` + `_CLIENT_SECRET` + stored refresh token                          | high         |
| `calendar`        | (new shape; see notes) | shares OAuth2 helper with `gmail`                                                          | high         |
| `make-viral-video`| make-viral-video       | depends on `image-generation`                                                              | medium       |

Gmail and Calendar are listed as new Windows-native shapes in
[`docs/skills-taxonomy.md`](skills-taxonomy.md) — the upstream `gws-*` /
`macos-tools` skills are macOS-only and replaced here by direct
`Google.Apis.Gmail.v1` / `Google.Apis.Calendar.v3` SDK use.

Out of scope for #13: any `[B]` cross-OS skill (`quota-tracker`,
`schedule-crons`, etc.) and any `[C]` Mac-only skill. Credential vault
(DPAPI/Keychain) is a separate follow-up; phase 1 uses environment
variables exclusively.

## Project layout

One new project: `src/Sutando.Skills.Cloud/`. References `Sutando.Skills` +
`Sutando.Workspace`. Each integration is a single `ISkill` class under
`Sutando.Skills.Cloud.<Group>/<Name>Skill.cs`, where `<Group>` is
`Google`, `OpenAI`, `Twitter`, or `Orchestration` (for `make-viral-video`).

A shared `Sutando.Skills.Cloud/Common/` folder holds helpers that two or
more skills consume: at minimum `GoogleOAuthHelper` (refresh-token flow
shared by `gmail` + `calendar`) and `ArtifactWriter` (write a binary
payload to `workspace/artifacts/<skill-id>/<timestamp>-<n>.<ext>` and
return the absolute path).

No separate `Sutando.Cloud.Abstractions` project. Introduce shared
interfaces only when a second consumer materialises — for now each skill
implements `ISkill` directly.

## Manifest discovery

Cloud skills are **assembly-registered**, not discovered from disk.
Rationale: they ship with the published `Sutando.Cli` package as managed
code. Users don't drop `skill.json` files for them; they're "built-in but
optional" — registered only when the required env vars are set.

Mechanism: a `CloudSkillRegistration` static class in
`Sutando.Skills.Cloud` exposes `Register(SkillRegistry registry,
IReadOnlyDictionary<string, string> env)`. It walks a fixed list of
`(envVarsRequired, factoryFunc)` tuples and calls
`registry.RegisterInstance(factory())` for each entry whose env vars are
all present. Skills with missing creds are silently skipped — the agent
gets a clean "this trigger is unknown" rather than an arm-and-fire on a
broken integration.

This is additive to filesystem discovery: the existing
`SkillDiscovery.Discover()` continues to handle on-disk `skill.json`
manifests, and `SkillRegistry.RegisterInstance` works alongside it.

## Manifest shape per skill

Each `ISkill` class exposes a static `DefaultManifest()` mirroring
`EchoSkill.DefaultManifest()`. Fields:

- `Id` — kebab-case skill name (matches the table above).
- `Name` — human-readable display name.
- `Description` — one sentence; surfaces in tool listings.
- `Version` — semver, bumped per behaviour change.
- `Runtime` — `Managed`.
- `Entry` — `Sutando.Skills.Cloud.<Group>.<Name>Skill, Sutando.Skills.Cloud`.
- `Triggers` — short keyword list the planner/agent matches against.
- `Capabilities` — at minimum `http-out`; add `fs-write` if the skill
  writes artifacts; add `audio` / `image` / `video` content tags as
  applicable.

## Artifact convention

Skills that produce binary output (TTS audio, generated images, video
clips) write to `workspace/artifacts/<skill-id>/`. Filenames use
`<utc-timestamp>-<short-hash>.<ext>` so concurrent invocations never
collide. The artifact path goes into `SkillResult.Artifacts` (already
plumbed through the upstream wire format — see `ScriptSkill` parser).
Skills that produce only text use `SkillResult.Body`.

## Env-var naming convention

Provider prefix + purpose, all uppercase, underscore-separated:

- `GEMINI_API_KEY` — already in use by Voice (`SUTANDO_VOICE_LOCAL` path)
  and matches the env vars listed in the project README. Reuse for any
  Gemini-family integration (`gemini-tts`, `image-generation`).
- `OPENAI_API_KEY` — new; follows OpenAI SDK's documented convention.
- `TWITTER_API_KEY` / `_API_SECRET` / `_ACCESS_TOKEN` / `_ACCESS_SECRET` —
  follows the X v2 OAuth1 vocabulary.
- `GOOGLE_OAUTH_CLIENT_ID` / `_CLIENT_SECRET` — shared between gmail and
  calendar. Refresh token persisted to `~/.sutando/credentials/google.json`
  (gitignored equivalent; written 0600 on POSIX, ACL-restricted on
  Windows). Token-storage detail belongs in the credential-vault
  follow-up but for phase 1 we ship a minimal-locked-file approach so
  these two skills are actually usable.

## Testing convention

Each skill ships with a fakes-backed unit test class in
`tests/Sutando.Tests/Skills/Cloud/`. The HTTP boundary is mocked via a
local `HttpMessageHandler` stub (the same pattern used by the existing
LocalInference HTTP adapters). Live-API integration tests use
`SkippableFact` gated on the relevant env var being present, mirroring
the `GeminiLiveIntegrationTests` pattern.

The `BashSkill_PlainTextStdoutBecomesBody` test was the last thing the
parser fix in commit `1f6d151` unblocked — that test pattern (driving a
skill end-to-end through `ISkill.ExecuteAsync` with fake context) is the
template.

## Wave ordering (sequential, not parallel)

Per the [advisor's note](https://github.com/anthropics/claude-code) on
this scope: no parallel agent dispatch — Wave A is implemented in order,
in-process, by the same author, so the pattern hardens with the first
skill rather than being re-invented three times.

1. **Reference (this wave):** `gemini-tts` — fully done with manifest,
   registration, fake-HTTP test, and live-API skippable test. Establishes
   project layout + `ArtifactWriter` + assembly-registration pattern.
2. **Wave A follow-on:** `openai-tts`, `image-generation` — mechanical
   ports of the reference shape against different endpoints.
3. **Wave B:** `x-twitter` — introduces OAuth1 helper.
4. **Wave C:** `gmail` (introduces `GoogleOAuthHelper`), then `calendar`
   (consumes it).
5. **Wave D:** `make-viral-video` — orchestrates `image-generation` plus
   ffmpeg shell-out.
6. **Wiring (separate task):** plumb the cloud `SkillRegistry`
   registration into `Sutando.Cli`'s host startup so the skills are
   reachable from the agent — currently no consumer wires the registry
   in.
