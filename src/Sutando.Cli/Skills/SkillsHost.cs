using Sutando.Notes;
using Sutando.Skills;
using Sutando.Skills.Cloud;
using Sutando.Skills.Discovery;
using Sutando.Workspace;

namespace Sutando.Cli.Skills;

/// <summary>
/// Helper that builds a fully-populated <see cref="SkillRegistry"/> for the CLI host. Combines
/// filesystem discovery (workspace + user <c>~/.sutando/skills/</c> roots) with the cloud-skill
/// assembly registrations gated on environment variables.
/// </summary>
internal static class SkillsHost
{
    /// <summary>
    /// Build and populate a <see cref="SkillRegistry"/> from the current process environment
    /// and the default discovery roots.
    /// </summary>
    /// <param name="workspace">Resolved workspace directory used for the workspace-local skills root.</param>
    /// <returns>
    /// A tuple of the registry plus a report indicating which skill IDs were discovered from disk
    /// and which were registered from the cloud-skills assembly.
    /// </returns>
    public static (SkillRegistry Registry, SkillsHostReport Report) BuildRegistry(WorkspaceDirectory workspace)
    {
        var env = CurrentEnvironment();
        return BuildRegistry(workspace, env);
    }

    /// <summary>
    /// Build and populate a <see cref="SkillRegistry"/> with an explicit environment dictionary.
    /// Used by tests to supply fake env vars without touching process state.
    /// </summary>
    /// <param name="workspace">Resolved workspace directory used for the workspace-local skills root.</param>
    /// <param name="env">Environment variable dictionary to consult for cloud-skill registration gating.</param>
    /// <returns>
    /// A tuple of the registry plus a report indicating which skill IDs were discovered from disk
    /// and which were registered from the cloud-skills assembly.
    /// </returns>
    public static (SkillRegistry Registry, SkillsHostReport Report) BuildRegistry(
        WorkspaceDirectory workspace,
        IReadOnlyDictionary<string, string> env)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(env);

        var registry = new SkillRegistry();

        // 1. Disk discovery: <workspace>/skills/ and ~/.sutando/skills/. When SUTANDO_PRIVATE_DIR
        //    is set, also scan <private-dir>/skills/ — mirrors upstream's loadSkillManifestTools()
        //    which scans both <repo>/skills and $SUTANDO_PRIVATE_DIR/skills. All four upstream-
        //    active manifest skills live in the private dir, so this is the primary use case.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var additionalRoots = new List<string>();
        if (env.TryGetValue("SUTANDO_PRIVATE_DIR", out var privateDir) && !string.IsNullOrWhiteSpace(privateDir))
        {
            additionalRoots.Add(Path.Combine(privateDir, "skills"));
        }
        var discovery = new SkillDiscovery(new SkillDiscoveryOptions
        {
            WorkspaceSkillsRoot = Path.Combine(workspace.Root.FullName, "skills"),
            UserSkillsRoot = Path.Combine(home, ".sutando", "skills"),
            AdditionalRoots = additionalRoots,
        });
        var discovered = discovery.Discover();
        registry.Register(discovered);
        var diskIds = discovered.Select(d => d.Manifest.Id).ToList();

        // 2. Assembly-level cloud skills — gated on the env vars each skill declares.
        var cloudIds = CloudSkillRegistration.Register(registry, env);

        // 3. Notes second-brain skills — always registered; backed by the workspace's notes/ dir.
        var notesStore = new FileSystemNoteStore(workspace);
        var notesService = new NotesService(notesStore);
        var notesIds = NotesSkillRegistration.RegisterAll(registry, notesService, notesStore);

        var report = new SkillsHostReport(diskIds, cloudIds, notesIds);
        return (registry, report);
    }

    private static IReadOnlyDictionary<string, string> CurrentEnvironment()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                dict[key] = value;
            }
        }
        return dict;
    }
}

/// <summary>
/// Report produced by <see cref="SkillsHost.BuildRegistry"/> describing the origin of each
/// registered skill.
/// </summary>
/// <param name="DiskIds">Skill IDs loaded from the workspace and user filesystem roots.</param>
/// <param name="CloudIds">Skill IDs registered from the cloud-skills assembly (env-var gated).</param>
/// <param name="NotesIds">Skill IDs registered from the notes second-brain library (always on).</param>
internal sealed record SkillsHostReport(
    IReadOnlyList<string> DiskIds,
    IReadOnlyList<string> CloudIds,
    IReadOnlyList<string> NotesIds);
