# Integration notes — channel-telegram

This worktree adds a new project `src/Sutando.Channels.Telegram/` but, per the task brief,
**leaves `Sutando.sln` unmodified**. Integrators need to follow up on the items below before
the channel ships as part of a full build / pack.

## Items the integrator must do at merge time

1. **Add the project to `Sutando.sln`.**
   ```
   dotnet sln Sutando.sln add src/Sutando.Channels.Telegram/Sutando.Channels.Telegram.csproj
   ```
   The project is currently pulled into the build graph only via `tests/Sutando.Tests`'s
   `<ProjectReference>`, which is enough to satisfy `dotnet build`/`dotnet test` on this
   worktree but does not propagate to `dotnet pack`. Adding it to the sln puts it in the
   default solution-level build, including CI publishes.

2. **Wire the channel into `Sutando.Cli`'s composition root.** The existing
   `Sutando.Channels.Cli.CliChatChannel` is constructed inside `src/Sutando.Cli/` (see how
   `WorkspaceDirectory.Resolve()` is threaded in there). The Telegram channel takes the
   same shape:

   ```csharp
   var options = new TelegramChannelOptions
   {
       BotToken = Environment.GetEnvironmentVariable("SUTANDO_TELEGRAM_TOKEN")!,
       OwnerUserId = ParseLong(Environment.GetEnvironmentVariable("SUTANDO_TELEGRAM_OWNER_ID")),
       VerifiedUserIds = ParseIdList(Environment.GetEnvironmentVariable("SUTANDO_TELEGRAM_VERIFIED_IDS")),
       TeamUserIds = ParseIdList(Environment.GetEnvironmentVariable("SUTANDO_TELEGRAM_TEAM_IDS")),
   };
   var channel = new TelegramChannel(workspace, options, loggerFactory.CreateLogger<TelegramChannel>());
   ```

   Activate it only when `SUTANDO_TELEGRAM_TOKEN` is present so the CLI degrades gracefully
   on hosts without Telegram configured.

3. **NuGet packaging.** `Sutando.Channels.Telegram.csproj` is packable (it inherits the
   shared metadata in `Directory.Build.props`). Once it lands in the sln, `dotnet pack`
   will produce a `Sutando.Channels.Telegram.<ver>.nupkg` alongside the other channel
   packages. No further plumbing required.

## Notes on the spec gaps

- **Upstream reference missing.** The task brief points at
  `..\..\ThirdParty\sutando\src\telegram-bridge.py` for parity, but no `ThirdParty/sutando/`
  exists in this checkout. The implementation follows the task brief's written behaviour
  contract instead: text + photo + voice-note + document ingestion, owner / verified / team /
  unverified access tiers, `[REPLIED]` / `[no-send]` / `[deduped:]` / `[file:]` markers,
  owner detection via `OwnerUserId`. If upstream lands later, sweep against it for any
  marker-grammar drift.

## What's deliberately deferred

- **Group / channel posts.** Only direct messages are processed today (the inbound mapper
  honours `msg.From?.Id`; group posts with no `From` collapse to `Unverified`). Group
  support is upstream parity-tracked but not in scope here.
- **Edit / reaction handling.** Inbound updates are restricted to `UpdateType.Message`. The
  upstream bridge similarly does not retry on edits — users resend instead.
- **Per-update retry / dead-letter.** Failed dispatch is logged and skipped; the offset
  advances so the bot doesn't loop on a poison update. A future enhancement could write
  failed updates to `<workspace>/data/telegram/failed/`.
