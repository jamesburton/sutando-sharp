using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Workspace;

namespace Sutando.Skills.Discovery;

/// <summary>
/// Filesystem-based skill discovery. Scans every configured root for
/// <c>&lt;root&gt;/&lt;skill-id&gt;/skill.json</c> and produces <see cref="DiscoveredSkill"/>
/// records that the loader / registry can then resolve into <see cref="ISkill"/> instances.
/// </summary>
/// <remarks>
/// Default scan order, highest precedence first:
/// <list type="number">
///   <item><description><c>&lt;workspace&gt;/skills/</c> — project-local skills (committed alongside the workspace).</description></item>
///   <item><description><c>~/.sutando/skills/</c> — per-user skills (installed by the operator).</description></item>
/// </list>
/// Additional roots can be supplied via <see cref="SkillDiscoveryOptions.AdditionalRoots"/>.
/// </remarks>
public sealed class SkillDiscovery
{
    private readonly SkillDiscoveryOptions _options;
    private readonly ILogger<SkillDiscovery> _logger;

    /// <param name="options">Configuration of scan roots.</param>
    /// <param name="logger">Optional logger.</param>
    public SkillDiscovery(SkillDiscoveryOptions options, ILogger<SkillDiscovery>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<SkillDiscovery>.Instance;
    }

    /// <summary>
    /// Build a discovery that uses the standard <c>&lt;workspace&gt;/skills</c> +
    /// <c>~/.sutando/skills</c> roots.
    /// </summary>
    public static SkillDiscovery Default(WorkspaceDirectory workspace, ILogger<SkillDiscovery>? logger = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new SkillDiscovery(new SkillDiscoveryOptions
        {
            WorkspaceSkillsRoot = Path.Combine(workspace.Root.FullName, "skills"),
            UserSkillsRoot = Path.Combine(home, ".sutando", "skills"),
        }, logger);
    }

    /// <summary>Scan every configured root and return one record per <c>skill.json</c> found.</summary>
    public IReadOnlyList<DiscoveredSkill> Discover()
    {
        var results = new List<DiscoveredSkill>();
        var seen = new HashSet<string>(StringComparer.Ordinal); // dedup by manifest id; first hit wins

        foreach (var root in EnumerateRoots())
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var skillDir in Directory.EnumerateDirectories(root))
            {
                var manifestPath = Path.Combine(skillDir, "skill.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                SkillManifest manifest;
                try
                {
                    manifest = SkillManifest.ParseFile(manifestPath);
                }
                catch (Exception ex) when (ex is IOException or FormatException)
                {
                    _logger.LogWarning(ex, "skipping malformed skill manifest at {Path}", manifestPath);
                    continue;
                }

                if (!seen.Add(manifest.Id))
                {
                    _logger.LogDebug(
                        "skill '{Id}' at {Path} shadowed by an earlier root — keeping the higher-precedence copy",
                        manifest.Id, skillDir);
                    continue;
                }

                results.Add(new DiscoveredSkill
                {
                    Manifest = manifest,
                    Root = skillDir,
                    ManifestPath = manifestPath,
                });
            }
        }

        return results;
    }

    private IEnumerable<string> EnumerateRoots()
    {
        if (_options.WorkspaceSkillsRoot is { Length: > 0 } ws)
        {
            yield return ws;
        }
        if (_options.UserSkillsRoot is { Length: > 0 } user)
        {
            yield return user;
        }
        foreach (var extra in _options.AdditionalRoots)
        {
            yield return extra;
        }
    }
}

/// <summary>Configuration for <see cref="SkillDiscovery"/>.</summary>
public sealed record SkillDiscoveryOptions
{
    /// <summary>Workspace-local skills root. Highest precedence (project ships its own).</summary>
    public string? WorkspaceSkillsRoot { get; init; }

    /// <summary>Per-user skills root. Second precedence (operator installs).</summary>
    public string? UserSkillsRoot { get; init; }

    /// <summary>Additional scan roots; lowest precedence, scanned in order.</summary>
    public IReadOnlyList<string> AdditionalRoots { get; init; } = [];
}

/// <summary>One result row from <see cref="SkillDiscovery.Discover"/>.</summary>
public sealed record DiscoveredSkill
{
    /// <summary>Parsed manifest.</summary>
    public required SkillManifest Manifest { get; init; }

    /// <summary>Filesystem root the skill was discovered under.</summary>
    public required string Root { get; init; }

    /// <summary>Absolute path of the <c>skill.json</c>.</summary>
    public required string ManifestPath { get; init; }
}
