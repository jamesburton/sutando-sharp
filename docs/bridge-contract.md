# Task/result bridge contract

This is the **frozen contract** that channel adapters, the agent executor, and the dashboard all agree on. Changes here cascade. Lock first, build everything else against it.

## Workspace layout

All mutable per-user state lives under one workspace directory:

```
<workspace>/
├── tasks/                       # pending tasks, written by any channel
│   ├── task-<id>.txt
│   ├── processed/               # tasks the executor has picked up but not yet completed
│   │   └── task-<id>.txt
│   └── archive/YYYY-MM/         # tasks completed (or cancelled) — kept for self-improvement
│       └── task-<id>.txt
├── results/                     # completed results, written by the executor
│   ├── task-<id>.txt            # mirrors the corresponding task id
│   ├── proactive-<ts>.txt       # agent-initiated speech with no inbound task
│   ├── question-<ts>.txt        # agent-asked questions awaiting user reply
│   └── archive/YYYY-MM/
├── state/
│   ├── cores/<host>.alive       # per-host heartbeat (json, atomic write)
│   ├── last-owner-activity.json # most recent owner interaction across channels
│   └── voice-session-context.json
├── core-status.json             # work signal: {"status":"running|idle","step":"...","ts":epoch}
├── notes/                       # second-brain markdown
├── logs/
└── data/
```

## Workspace resolution

Every process resolves the workspace the same way:

1. `$SUTANDO_WORKSPACE` (env var, `~` expanded).
2. `~/.sutando/workspace/` (default, OS-neutral).

The default deliberately avoids `~/Library/Application Support/sutando/` on macOS (that's the packaged app's territory) and `%APPDATA%\sutando\` on Windows (reserved for future native app).

Legacy installs that polled the repo root for tasks/ are migrated once on first run by `WorkspaceDirectory.MigrateLegacyAsync`, mirroring upstream's `_migrate_from_legacy`.

## Task envelope

Each task is a UTF-8 text file with `key: value` lines, terminated by `\n`. The body (`task:`) is the only multi-line-tolerant field: everything after `task:` up to the next `key:` line counts.

Required fields:

| Field | Type | Notes |
|---|---|---|
| `id` | string | `task-<source>-<unix-ms>` convention; unique per workspace. |
| `timestamp` | ISO-8601 UTC | Source-time of submission. |
| `task` | string (possibly multi-line) | Free-form work description. |
| `source` | enum | `voice` \| `chat` \| `telegram` \| `discord` \| `phone` \| `api` \| `cron` \| `health` \| `proactive`. |
| `channel_id` | string | Source-specific identifier (e.g. `local-voice`, `local-chat`, telegram chat ID, discord channel ID). |
| `user_id` | string | Originating user; `chat-local` for local-chat sessions. |
| `access_tier` | enum | `owner` \| `verified` \| `team` \| `other` \| `unverified`. |
| `priority` | enum | `urgent` (voice/phone, sub-second target) \| `normal` (chat/owner DM, default) \| `low` (cron/health/non-owner). |

Optional fields:

| Field | Notes |
|---|---|
| `timeout_ms` | Per-task wall-clock budget; default 10 min. |
| `dm_on_timeout` | `true`/`false`; whether to DM owner on timeout. Default false. |
| `reply_to_message_id` | When applicable (telegram/discord). |
| `meta.*` | Reserved namespace for source-specific extras. |

Cancellation is a regular task whose `task:` body starts with `CANCEL_INSTRUCTION: <target-task-id>`. The executor treats it as a signal — terminates the referenced task if in-flight, writes a one-line confirm result, and does not process the body further.

## Result envelope

Each result is a UTF-8 text file in `results/<task-id>.txt`. The body is free text, but the **first non-whitespace token** can be a marker that changes delivery semantics:

| Marker | Effect |
|---|---|
| `[deduped: task-<other-id>]` | Archive silently; canonical reply lives in the other task. |
| `[no-send]` | Archive without delivering anything to the channel. |
| `[REPLIED]` | Skip delivery — already sent through another path. |
| `[file: /path]` / `[send: /path]` / `[attach: /path]` | Attach the file alongside the text body when the channel supports it. |

Markers may stack (`[file: ...]\n[file: ...]` for multiple attachments).

Proactive (agent-initiated) results land at `results/proactive-<ts>.txt`. Questions awaiting user input land at `results/question-<ts>.txt`.

## Archive layout

On completion, both the source task file and the result file move to `tasks/archive/YYYY-MM/` and `results/archive/YYYY-MM/` respectively. Month-partitioning matches upstream PR #591. Legacy flat-archive (`tasks/archive/<id>.txt`) is read but no longer written.

## Heartbeat

Each running `sutando` process writes `state/cores/<hostname>.alive` every 30 s. mtime is the cross-host liveness probe (younger than ~90 s → alive). On graceful shutdown the file is unlinked so peers see the offline transition immediately.

Payload:

```json
{
  "host": "...",
  "pid": 1234,
  "started_at": 1747500000,
  "last_beat_at": 1747500030,
  "status": "idle|running|degraded",
  "schema_version": 1
}
```

## Work-status signal

`core-status.json` (single file, atomic write) signals what the agent is doing right now:

```json
{ "status": "running", "step": "summarising email", "ts": 1747500041 }
{ "status": "idle", "ts": 1747500042 }
```

The dashboard polls this; channel adapters never write to it.

## Owner activity

`state/last-owner-activity.json` records the most recent owner interaction across all channels — used by the proactive loop's "don't interrupt the human" rule:

```json
{ "ts": 1747500045, "channel": "voice", "summary": "first 80 chars of last task" }
```

## Default priorities per source

| Source | Default priority |
|---|---|
| `voice`, `phone` | `urgent` |
| `chat` (owner), `telegram` (owner), `discord` (owner) | `normal` |
| `api` | `normal` |
| `cron`, `health`, `proactive`, non-owner DMs | `low` |

The dispatcher picks highest-priority pending task; ties broken by mtime FIFO.
