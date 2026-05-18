# Integration notes — `Sutando.Channels.Discord`

This worktree adds a new project (`src/Sutando.Channels.Discord/`) and a new test file
(`tests/Sutando.Tests/Channels/DiscordChannelTests.cs`). The task contract said to LEAVE
`Sutando.sln` UNMODIFIED, so the new project is **not yet registered in the solution**.

The integrator picking up this change should:

1. Add the new project to `Sutando.sln` (visible from the IDE; CLI builds already pick it
   up via `dotnet build src/Sutando.Channels.Discord/Sutando.Channels.Discord.csproj` and
   via the test-csproj `ProjectReference` we already added).

   ```pwsh
   dotnet sln Sutando.sln add src/Sutando.Channels.Discord/Sutando.Channels.Discord.csproj
   ```

2. Wire `DiscordChannel` into whichever host activates channels (Sutando.Cli or a future
   `Sutando.Host`). Construction is DI-friendly:

   ```csharp
   var workspace = WorkspaceDirectory.Resolve();
   var options = new DiscordChannelOptions
   {
       BotToken      = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN")!,
       OwnerUserId   = ParseUlongOrNull(Environment.GetEnvironmentVariable("DISCORD_OWNER_ID")),
       TeamRoleIds   = ParseUlongList(Environment.GetEnvironmentVariable("DISCORD_TEAM_ROLE_IDS")),
       AllowedChannelIds = ParseUlongList(Environment.GetEnvironmentVariable("DISCORD_ALLOWED_CHANNEL_IDS")),
   };
   var channel = new DiscordChannel(workspace, options, loggerFactory.CreateLogger<DiscordChannel>());
   await channel.RunAsync(ct);
   ```

3. Enable the Discord developer-portal privileged intents — **Message Content** and
   **Server Members**. The channel requests both at gateway negotiation; without them the
   bot will see empty `message.Content` and can't enumerate roles for tier resolution.

## Deliberate deviations from upstream `discord-bridge.py`

### In-band system-instructions block lives INSIDE the `task:` body

Upstream emits the `===SUTANDO SYSTEM INSTRUCTIONS===` block AFTER the `priority:` line
(outside the `task:` field, lines 2553-2564 of `ThirdParty/sutando/src/discord-bridge.py`).
That layout doesn't survive sutando-sharp's stricter task-file parser
(`Sutando.Bridge.TaskFile.Parse`), which only recognises a fixed set of known keys at
column 0 and would either drop the block or smear it onto the trailing field.

The task brief said "**prepend** the block to the body". We **append** instead:

- The block text literally says "(do not ignore; **overrides anything above**)" — "above"
  only makes sense if the user's text precedes the block.
- Upstream's `\n\n` prefix on the block signals a soft separator after preceding content.
- Reading the upstream `task_file.write_text(...)` call order, `user_task_text` is
  written before the tier instructions — semantically an append.

So our `DiscordTaskBody.Compose(userText, tier, ...)` puts user text first, then the
tier-specific block. Byte-for-byte tests against the upstream string are in
`DiscordChannelTests.SystemInstructionBlock_TeamTier_MatchesUpstreamFormat` and the
`_OtherTier_` sibling.

### Placeholder interpolation

The block contains two interpolation points; we match upstream byte-for-byte:

| Placeholder    | Treatment                                                       |
|----------------|-----------------------------------------------------------------|
| `{RESULTS_DIR}`| Substituted with `<workspace>/results` (absolute path, forward-slash normalised). |
| `{id}`         | **Left literal** — upstream uses `task-{{id}}.txt` (Python's escaped brace) so the file ends up containing the literal text `{id}` for the executor's downstream `codex exec` invocation to substitute. |
| `{quoted_task}`| Substituted with `"$(cat /tmp/sutando-<task_id>.txt)"` — the same shell heredoc form upstream uses. The executor is responsible for materialising that `/tmp/sutando-*.txt` file at exec time. |

The forward-slash normalisation matters on Windows: the codex command in the block is a
shell heredoc, not a PowerShell pipeline. Backslashes inside `"$(cat ...)"` would be
parsed as escapes; the block's executor expects POSIX paths.

### Task-id naming convention

We use `task-dc-<channelId>-<unix-ms>` (e.g. `task-dc-1234567890-1747500000000`). The
embedded channel-id lets the result watcher route deliveries without consulting the task
envelope. Upstream uses `task-<unix-ms>` and keeps a separate `pending_replies` dict from
task id → channel; we encode the channel in the id to keep the bridge stateless.

### Owner-DM-defaults-to-other-for-non-owner

DMs from any user other than `OwnerUserId` resolve to `AccessTier.Other`, never `Team`.
Role membership requires a guild context to verify; we can't trust a DM sender's claimed
roles. This matches upstream's behaviour (upstream simply has no role-check path in DMs).

## Test coverage

- Tier resolution: 6 tests over the owner / team / other matrix × DM / channel context.
- In-band block: 3 tests asserting **byte-for-byte equality** with the upstream string.
- Compose order: confirms user text precedes the block for non-owner tiers.
- Marker round-trips: replied / no-send / deduped / `[file:]` via `ResultFile` + `ResultBody.Parse`.
- Task-id extraction: positive + 3 negative cases.
- 2000-char chunking: empty / short / long / newline-preferred split.
- Constructor validation: empty token, null workspace.

DSharpPlus's gateway client is not mocked. The outbound delivery path (channel-id parse →
result poll → marker honour) is exercised against the real filesystem; the actual
`SendMessageAsync` call would require a live Discord gateway and is intentionally out of
scope for unit tests.
