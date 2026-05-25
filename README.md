# sutando-sharp

A .NET 10 port of [sutando](https://github.com/sonichi/sutando) — a personal AI agent — packaged for zero-install execution via `dnx`.

## Status

🚧 **Alpha.** Core spine + most channels + voice transport + browser + Windows platform adapter + skills runtime have landed; remaining work is the local-inference stack, phone bridge, optional cloud integrations, cross-OS hardening. Cross-platform from day 1 (Windows-first; macOS / Linux adapters TBD).

## What sutando is

A personal AI agent that runs alongside you: voice, screen, meetings, chat, phone. A central **task bridge** (`<workspace>/tasks/` → `results/`) coordinates inbound channels, the agent executor, and outbound delivery. Cloud reasoning today (Anthropic Claude Code CLI or direct API, Google Gemini Live for realtime voice); a fully local pipeline is planned (see [`docs/local-stack.md`](docs/local-stack.md)).

Upstream is macOS-only TypeScript + Python + Swift. This port reimplements in C# with platform abstractions so the same managed core runs anywhere .NET 10 runs.

## What's in the box right now

| Project | Role |
|---|---|
| `Sutando.Cli` | The `dnx`-runnable tool — 16 subcommands. See [`docs/cli.md`](docs/cli.md). |
| `Sutando.Workspace` | Canonical workspace path resolution + heartbeat + core-status + owner-activity |
| `Sutando.Bridge` | Locked task/result contract, marker grammar, archive, file-watcher |
| `Sutando.Core` | `IAgentExecutor` abstraction + Claude CLI executor + Anthropic HTTP executor + `TaskRunner` |
| `Sutando.Realtime` | Gemini Live transport + `VoiceSession` state machine + tool-call dispatch |
| `Sutando.Voice` | WebSocket fan-out server on `:9900`, JSON envelope wire protocol |
| `Sutando.Api` | HTTP task-submission API on `:7843` (bearer auth via `SUTANDO_API_TOKEN`) |
| `Sutando.Dashboard` | Read-only status dashboard on `:7844` with SignalR live push |
| `Sutando.Channels.Abstractions` | `IChannel` contract |
| `Sutando.Channels.Cli` | Local interactive REPL chat channel |
| `Sutando.Channels.Telegram` | Telegram bot bridge (`Telegram.Bot` 22.10) |
| `Sutando.Channels.Discord` | Discord bot bridge (`DSharpPlus` 4.5.2) with byte-for-byte upstream tier sandboxing |
| `Sutando.Browser` | Playwright wrapper matching upstream's action grammar |
| `Sutando.Skills` | Skill manifest + discovery + managed/script runtimes + registry |
| `Sutando.Skills.Cloud` | Optional cloud-API skills (Gemini TTS, OpenAI TTS, image gen, X/Twitter tweet, gmail, calendar, viral video) — gated on env vars |
| `Sutando.Platform.Abstractions` | Cross-platform contracts: screen capture, clipboard, notifications, hotkeys |
| `Sutando.Platform.Windows` | Windows-specific implementations (GDI capture, toast, Win32 clipboard, RegisterHotKey) |
| `Sutando.Tests` | xunit, multi-targeted on Windows for platform-specific tests |

**448 tests passing, 12 skipped, 0 failed** across both TFMs (`net10.0` + `net10.0-windows10.0.19041.0`).

## Why a .NET port

- **Cross-platform from one toolchain.** Windows / macOS / Linux share the same managed core; only screen capture / hotkeys / notifications get platform-specific adapters.
- **Zero-install via `dnx`.** Users run `dnx Sutando.Cli` (or `dotnet dnx Sutando.Cli`) to execute the published version without a local install. After `dotnet tool install -g Sutando.Cli` it's just `sutando`.
- **Pluggable agent executor.** Talk to the user's existing Claude Code subscription via CLI shell-out, or hit the Anthropic API directly — same `IAgentExecutor` interface.

## Run (once published)

```pwsh
# Run the latest published tool without installing it
dotnet dnx Sutando.Cli --prerelease

# Or install once, run anywhere
dotnet tool install -g Sutando.Cli
sutando --help
```

## Build from source

```pwsh
dotnet build Sutando.sln -c Release
dotnet test  tests/Sutando.Tests/Sutando.Tests.csproj -c Release --no-build
dotnet pack  src/Sutando.Cli/Sutando.Cli.csproj      -c Release --no-build -o nuget-local
dotnet dnx   Sutando.Cli --source ./nuget-local --prerelease -- --help
```

## Configuration

| Env var | Used by |
|---|---|
| `SUTANDO_WORKSPACE` | Overrides the default `~/.sutando/workspace/` |
| `SUTANDO_API_TOKEN` | Bearer token for `sutando api`. Unset = open API + warning at startup. |
| `GEMINI_VOICE_API_KEY` / `GEMINI_API_KEY` | Voice WS server reads `GEMINI_VOICE_API_KEY` first, falls back to `GEMINI_API_KEY`. |
| `TELEGRAM_BOT_TOKEN` + `TELEGRAM_OWNER_USER_ID` + `TELEGRAM_VERIFIED_USER_IDS` + `TELEGRAM_TEAM_USER_IDS` | `sutando telegram` |
| `DISCORD_BOT_TOKEN` + `DISCORD_OWNER_USER_ID` + `DISCORD_TEAM_ROLE_IDS` + `DISCORD_ALLOWED_CHANNEL_IDS` | `sutando discord` |
| `SUTANDO_DM_OWNER_ID` | Optional override for the local-chat user-id |
| `SUTANDO_API_PORT` / `SUTANDO_DASHBOARD_PORT` / `SUTANDO_VOICE_PORT` | Per-service port overrides |

## Architecture docs

- [`docs/bridge-contract.md`](docs/bridge-contract.md) — locked task/result file format + marker grammar + archive layout
- [`docs/cli.md`](docs/cli.md) — full CLI subcommand reference
- [`docs/skills-taxonomy.md`](docs/skills-taxonomy.md) — classification of upstream's 27 skills into port / reimplement / defer buckets
- [`docs/bodhi-scope.md`](docs/bodhi-scope.md) — scope analysis for porting `bodhi-realtime-agent` (the voice layer)
- [`docs/local-stack.md`](docs/local-stack.md) — planned local-inference pipeline (Qwen3-8B + faster-whisper + Kokoro + Pipecat-or-port)
- [`docs/local-stack-scope.md`](docs/local-stack-scope.md) — recon answers for the local-stack open questions
- [`docs/platform-strategy.md`](docs/platform-strategy.md) — MAUI consideration + per-OS adapter strategy
- [`docs/voice-wire-protocol.md`](docs/voice-wire-protocol.md) — `/voice` WebSocket JSON envelope protocol

## Relationship to upstream

This is an independent rewrite under the same MIT license. Upstream tracked at `..\ThirdParty\sutando\`. We follow upstream's architecture (file-bridge core, channel adapters, skill ecosystem, 3-tier access) but reimplement in C# with cross-platform abstractions from day 1.

## License

MIT — same as upstream.
