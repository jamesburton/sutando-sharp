using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Skills.Discovery;
using Sutando.Skills.Runtimes;

namespace Sutando.Skills;

/// <summary>
/// Aggregator over discovered skills. Resolves manifests into live <see cref="ISkill"/>
/// instances on demand and looks them up by id or trigger keyword.
/// </summary>
public sealed class SkillRegistry
{
    private readonly Dictionary<string, RegistryEntry> _byId = new(StringComparer.Ordinal);
    private readonly ManagedSkillFactory _managedFactory;
    private readonly ScriptSkillRunnerOptions _scriptOptions;
    private readonly ILogger<SkillRegistry> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <param name="loggerFactory">Used to build per-skill loggers for <see cref="ScriptSkill"/>.</param>
    /// <param name="scriptOptions">Configuration for script runtimes (binary paths, timeout).</param>
    public SkillRegistry(
        ILoggerFactory? loggerFactory = null,
        ScriptSkillRunnerOptions? scriptOptions = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<SkillRegistry>();
        _scriptOptions = scriptOptions ?? new ScriptSkillRunnerOptions();
        _managedFactory = new ManagedSkillFactory(_loggerFactory.CreateLogger<ManagedSkillFactory>());
    }

    /// <summary>Bulk-register every record produced by a discovery scan.</summary>
    /// <param name="discovered">Output of <see cref="SkillDiscovery.Discover"/>.</param>
    public void Register(IEnumerable<DiscoveredSkill> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        foreach (var d in discovered)
        {
            Register(d);
        }
    }

    /// <summary>Register a single discovered skill. Later registrations of the same id replace earlier ones.</summary>
    public void Register(DiscoveredSkill discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        _byId[discovered.Manifest.Id] = new RegistryEntry(discovered, null);
        _logger.LogDebug("registered skill '{Id}' from {Root}", discovered.Manifest.Id, discovered.Root);
    }

    /// <summary>Register an already-instantiated managed skill — useful for built-ins compiled into the host.</summary>
    public void RegisterInstance(ISkill skill, string? skillRoot = null)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var discovered = new DiscoveredSkill
        {
            Manifest = skill.Manifest,
            Root = skillRoot ?? AppContext.BaseDirectory,
            ManifestPath = string.Empty,
        };
        _byId[skill.Manifest.Id] = new RegistryEntry(discovered, skill);
        _logger.LogDebug("registered built-in skill '{Id}'", skill.Manifest.Id);
    }

    /// <summary>All registered manifests, in registration order.</summary>
    public IReadOnlyCollection<SkillManifest> Manifests => _byId.Values.Select(e => e.Discovered.Manifest).ToList();

    /// <summary>Resolve a skill by id. Throws if not registered.</summary>
    public ISkill Get(string id) => TryGet(id)
        ?? throw new KeyNotFoundException($"skill '{id}' is not registered.");

    /// <summary>Resolve a skill by id, or <see langword="null"/> when absent.</summary>
    public ISkill? TryGet(string id)
    {
        if (!_byId.TryGetValue(id, out var entry))
        {
            return null;
        }
        if (entry.Instance is not null)
        {
            return entry.Instance;
        }
        var instance = Instantiate(entry.Discovered);
        _byId[id] = entry with { Instance = instance };
        return instance;
    }

    /// <summary>Return every skill whose <see cref="SkillManifest.Triggers"/> contains the given keyword (case-insensitive).</summary>
    public IReadOnlyList<ISkill> ResolveByTrigger(string trigger)
    {
        var matches = new List<ISkill>();
        foreach (var (id, entry) in _byId)
        {
            var triggers = entry.Discovered.Manifest.Triggers;
            for (var i = 0; i < triggers.Count; i++)
            {
                if (string.Equals(triggers[i], trigger, StringComparison.OrdinalIgnoreCase))
                {
                    var instance = TryGet(id);
                    if (instance is not null)
                    {
                        matches.Add(instance);
                    }
                    break;
                }
            }
        }
        return matches;
    }

    private ISkill Instantiate(DiscoveredSkill discovered) => discovered.Manifest.Runtime switch
    {
        SkillRuntime.Managed => _managedFactory.Create(discovered),
        SkillRuntime.Python or SkillRuntime.Node or SkillRuntime.Bash or SkillRuntime.DotnetTool =>
            new ScriptSkill(discovered.Manifest, discovered.Root, _scriptOptions, _loggerFactory.CreateLogger<ScriptSkill>()),
        _ => throw new InvalidOperationException($"unsupported skill runtime '{discovered.Manifest.Runtime}' for '{discovered.Manifest.Id}'"),
    };

    private sealed record RegistryEntry(DiscoveredSkill Discovered, ISkill? Instance);
}
