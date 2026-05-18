# `sutando` — CLI reference

The CLI is the operator-facing front door for every Sutando capability: the
workspace, the bridge, channel adapters, the voice / API / dashboard servers,
the browser automation runner. Each subcommand is one function in
[`Commands.cs`](../src/Sutando.Cli/Commands.cs); the dispatch table lives in
[`Program.cs`](../src/Sutando.Cli/Program.cs).

Run any of these via:

- `sutando <verb>` — once installed (`dotnet tool install -g Sutando.Cli`)
- `dotnet dnx Sutando.Cli -- <verb>` — without installing, against the published package
- `dotnet dnx Sutando.Cli --source ./nuget-local --prerelease -- <verb>` — against a locally-packed build

## Workspace + spine

| Verb | What |
|---|---|
| `sutando workspace` | Print the resolved workspace dir and key paths (`tasks/`, `results/`, `state/`, `core-status.json`). |
| `sutando work <task>` | Submit a chat-source task into `<workspace>/tasks/task-chat-<ms>.txt`. |
| `sutando watch` | Watch `tasks/` for new envelopes; print arrivals (id, source, priority, tier, user, body preview). Ctrl+C to stop. |
| `sutando results tail` | Tail `results/`; parse marker prefixes (`[deduped:]`, `[no-send]`, `[REPLIED]`, `[file:]`) and print bodies. |
| `sutando status [--watch]` | Show the parsed `core-status.json` payload. `--watch` re-polls every 2 s. |
| `sutando heartbeat [--start]` | Write a single heartbeat to `state/cores/<host>.alive`. `--start` runs the loop until Ctrl+C. |
| `sutando version` | Print the running version. |
| `sutando help` | Print the help banner. |
| `sutando hello [name]` | Used to verify the `dnx` round-trip on first install. |

## Channels

Each channel adapter writes inbound user messages into the bridge as task
envelopes and polls `results/` to deliver responses back out the originating
transport. Configuration is via env vars; the CLI verb is the host loop.

| Verb | Required env | Optional env |
|---|---|---|
| `sutando chat [--timeout <s>]` | (none) | — |
| `sutando telegram` | `TELEGRAM_BOT_TOKEN` | `TELEGRAM_OWNER_USER_ID`, `TELEGRAM_VERIFIED_USER_IDS`, `TELEGRAM_TEAM_USER_IDS` (comma-separated) |
| `sutando discord` | `DISCORD_BOT_TOKEN` | `DISCORD_OWNER_USER_ID`, `DISCORD_TEAM_ROLE_IDS`, `DISCORD_ALLOWED_CHANNEL_IDS` (comma-separated) |

`chat` is the local REPL — type a line, see the matching result; `:exit` /
`:quit` / Ctrl+D exit cleanly; `:status` and `:tasks` are inspection
shortcuts. `telegram` and `discord` long-poll their respective platforms
and write `task-tg-<chatId>-<ms>.txt` / `task-dc-<channelId>-<ms>.txt`
envelopes. The 3-tier access model (`owner` / `verified` / `team` / `other`
/ `unverified`) is enforced by the adapter; non-owner tasks get an in-band
`===SUTANDO SYSTEM INSTRUCTIONS===` block that routes them through a
sandboxed executor.

## Servers

| Verb | Port | What |
|---|---|---|
| `sutando voice` | 9900 | WebSocket fan-out at `/voice`. One `VoiceSession` per browser connection, bridging to Gemini Live. `/healthz` reports session count. Wire protocol documented in [`voice-wire-protocol.md`](voice-wire-protocol.md). |
| `sutando api` | 7843 | HTTP task submission. `POST /tasks` writes an envelope; `GET /tasks/{id}` returns task + result; `GET /tasks` lists pending; `GET /status` echoes `core-status.json`. Bearer auth via `SUTANDO_API_TOKEN`; `/healthz` is always open. |
| `sutando dashboard` | 7844 | Read-only status page with SignalR live push for `core_status_changed`, `task_added`, `result_added`. No auth — local read-only by intent. |

Port overrides: `--port <n>` flag, `SUTANDO_VOICE_PORT` / `SUTANDO_API_PORT` / `SUTANDO_DASHBOARD_PORT` env vars.

## Browser

| Verb | What |
|---|---|
| `sutando browser <url> [action]...` | Drive a Playwright browser session. Actions are colon-delimited verbs matching upstream `src/browser.mjs`: `text`, `screenshot`, `pdf`, `html`, `click:<sel>`, `fill:<sel>:<value>`, `select:<sel>:<value>`, `wait:<ms>`. With no actions, dumps page text. |

Browser binaries are downloaded on first use — `pwsh bin/Debug/net10.0/playwright.ps1 install` if you want them upfront.

## Workspace resolution

Every subcommand resolves the workspace the same way:

1. `$SUTANDO_WORKSPACE` env var (override; `~` is expanded).
2. `~/.sutando/workspace/` (default, OS-neutral).

See [`bridge-contract.md`](bridge-contract.md) for the on-disk layout and the locked task/result file format.
