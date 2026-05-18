# Integration notes — local CLI chat channel

This worktree adds a new project `Sutando.Channels.Cli` plus a `CliChatChannel`
implementation, but **does not** wire the new project into `Sutando.sln` or
`Sutando.Cli`. Those are merge-time integration steps so an integrator can
review the additions in isolation before exposing the `chat` subcommand. Three
edits are needed at merge time.

## 1. Add the project to `Sutando.sln`

Insert a new `Project(…)` block alongside the other src projects, configure it
in `ProjectConfigurationPlatforms`, and nest it under the `src` solution folder.

A fresh GUID has been pre-generated for convenience:
`{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}` — feel free to regenerate if you'd
prefer a different one (just keep the same value across all three sections
below).

### 1a. Add the `Project(…) … EndProject` declaration

Insert after the existing `Sutando.Platform.Abstractions` block (before the
`tests` folder block):

```text
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sutando.Channels.Cli", "src\Sutando.Channels.Cli\Sutando.Channels.Cli.csproj", "{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}"
EndProject
```

### 1b. Add to `ProjectConfigurationPlatforms`

Inside `GlobalSection(ProjectConfigurationPlatforms) = postSolution`, add (the
existing entries follow the same Debug|x64 / x86 / Any CPU pattern):

```text
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}.Debug|Any CPU.Build.0 = Debug|Any CPU
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}.Debug|x64.ActiveCfg = Debug|Any CPU
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}.Debug|x64.Build.0 = Debug|Any CPU
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}.Debug|x86.ActiveCfg = Debug|Any CPU
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}.Debug|x86.Build.0 = Debug|Any CPU
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}.Release|Any CPU.ActiveCfg = Release|Any CPU
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}.Release|Any CPU.Build.0 = Release|Any CPU
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}.Release|x64.ActiveCfg = Release|Any CPU
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}.Release|x64.Build.0 = Release|Any CPU
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}.Release|x86.ActiveCfg = Release|Any CPU
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5}.Release|x86.Build.0 = Release|Any CPU
```

### 1c. Add to `NestedProjects`

The `src` solution folder GUID is `{827E0CD3-B72D-47B6-A68D-7590B98EB39B}`:

```text
{9085C25F-8F45-4C93-8106-C30AF5A8AFC5} = {827E0CD3-B72D-47B6-A68D-7590B98EB39B}
```

## 2. Add the project reference to `src/Sutando.Cli/Sutando.Cli.csproj`

Inside the existing `<ItemGroup>` that already holds the other project
references, add one more line:

```xml
<ProjectReference Include="..\Sutando.Channels.Cli\Sutando.Channels.Cli.csproj" />
```

## 3. Wire up the `chat` subcommand in `src/Sutando.Cli/Commands.cs` and `Program.cs`

### 3a. Add a new method to `Commands` (in `Commands.cs`)

```csharp
public static async Task<int> ChatAsync(string version, string[] args)
{
    var ws = WorkspaceDirectory.Resolve();

    // --timeout <seconds> (default 5 min).
    var timeout = TimeSpan.FromMinutes(5);
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--timeout"
            && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs)
            && secs > 0)
        {
            timeout = TimeSpan.FromSeconds(secs);
            break;
        }
    }

    var channel = new Sutando.Channels.Cli.CliChatChannel(
        ws,
        new Sutando.Channels.Cli.CliChatChannelOptions
        {
            Version = version,
            ResultTimeout = timeout,
        });
    using var cts = NewSigIntCts();
    await channel.RunAsync(cts.Token).ConfigureAwait(false);
    return 0;
}
```

(`NewSigIntCts` is already declared in `Commands.cs`.)

### 3b. Add a case to the dispatch switch in `Program.cs`

```csharp
"chat" => await Commands.ChatAsync(version, args).ConfigureAwait(false),
```

### 3c. Document it in the help banner (also in `Program.cs`)

Add a line under `COMMANDS`:

```text
chat [--timeout <s>]      Interactive REPL: send chat tasks and wait for results.
```

## After merging

Build & test as usual:

```pwsh
dotnet build Sutando.sln
dotnet test tests/Sutando.Tests/
dotnet run --project src/Sutando.Cli -- chat
```
