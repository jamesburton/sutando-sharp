using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Workspace;

namespace Sutando.Proactive;

/// <summary>
/// Loads <see cref="CronEntry"/> definitions from a workspace's <c>crons.json</c>, with a
/// <c>crons.example.json</c> fallback that mirrors upstream's first-run behaviour.
/// </summary>
/// <remarks>
/// Resolution order, in priority:
/// <list type="number">
///   <item><description><c>{workspace-root}/crons.json</c> — the personal, gitignored config.</description></item>
///   <item><description><c>{workspace-root}/crons.example.json</c> — the template shipped alongside the example file (matches upstream <c>cp crons.example.json crons.json</c> bootstrap).</description></item>
/// </list>
/// <para>
/// When neither file exists the loader returns an empty list — a fresh install with no
/// scheduled work is a legitimate state, not an error.
/// </para>
/// </remarks>
public sealed class CronConfigLoader
{
    /// <summary>The canonical filename inside the workspace root.</summary>
    public const string PrimaryFileName = "crons.json";

    /// <summary>The template filename consulted when the primary file is absent.</summary>
    public const string FallbackFileName = "crons.example.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<CronConfigLoader> _logger;

    /// <summary>Initializes a new loader.</summary>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger{T}"/>.</param>
    public CronConfigLoader(ILogger<CronConfigLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<CronConfigLoader>.Instance;
    }

    /// <summary>
    /// Loads cron entries from the given workspace root. Honours the resolution order
    /// documented on the class. Invalid entries (missing name / cron / both-or-neither
    /// prompt fields) are filtered out with a warning so a single bad row doesn't blank the
    /// whole schedule.
    /// </summary>
    /// <param name="workspace">The workspace to read from.</param>
    /// <returns>The loaded, validated list of cron entries (possibly empty).</returns>
    public IReadOnlyList<CronEntry> Load(WorkspaceDirectory workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Load(workspace.Root.FullName);
    }

    /// <summary>
    /// Loads cron entries from an explicit workspace-root path. Useful for tests that don't
    /// want to stand up a full <see cref="WorkspaceDirectory"/>.
    /// </summary>
    /// <param name="workspaceRoot">Directory containing <c>crons.json</c> / <c>crons.example.json</c>.</param>
    /// <returns>The loaded, validated list of cron entries (possibly empty).</returns>
    public IReadOnlyList<CronEntry> Load(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var primary = Path.Combine(workspaceRoot, PrimaryFileName);
        if (File.Exists(primary))
        {
            return LoadFromFile(primary);
        }

        var fallback = Path.Combine(workspaceRoot, FallbackFileName);
        if (File.Exists(fallback))
        {
            _logger.LogInformation(
                "crons.json not found; falling back to {Fallback}. Copy it to crons.json to customise.",
                FallbackFileName);
            return LoadFromFile(fallback);
        }

        _logger.LogInformation(
            "No cron config found at {Primary} or {Fallback}; scheduling will be inert.",
            primary,
            fallback);
        return Array.Empty<CronEntry>();
    }

    private IReadOnlyList<CronEntry> LoadFromFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var raw = JsonSerializer.Deserialize<List<CronEntry>>(stream, SerializerOptions)
                ?? new List<CronEntry>();

            var valid = new List<CronEntry>(raw.Count);
            foreach (var entry in raw)
            {
                if (entry is null)
                {
                    continue;
                }

                if (!entry.IsValid())
                {
                    _logger.LogWarning(
                        "Skipping invalid cron entry {Name} in {Path}: requires non-empty name, cron, and exactly one of prompt / prompt_skill.",
                        entry.Name ?? "(unnamed)",
                        path);
                    continue;
                }

                valid.Add(entry);
            }

            return valid;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse cron config {Path}; treating as empty.", path);
            return Array.Empty<CronEntry>();
        }
    }
}
