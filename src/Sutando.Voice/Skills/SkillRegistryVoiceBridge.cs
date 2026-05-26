// Lives in Sutando.Voice (not Sutando.Realtime) because Sutando.Phone also consumes Sutando.Realtime
// and must not be pulled into a Sutando.Skills dependency it does not need.
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Realtime;
using Sutando.Skills;
using Sutando.Workspace;

namespace Sutando.Voice.Skills;

/// <summary>
/// Bridges a <see cref="SkillRegistry"/> onto the realtime tool surface used by
/// <see cref="VoiceSession"/>. Per-session snapshot: the registry is iterated once at bridge
/// construction (or whenever <see cref="GetToolDefinitions"/> is read) and each entry is wrapped
/// in a <see cref="SkillVoiceTool"/>. The bridge then exposes the resulting
/// <see cref="RealtimeToolDefinition"/>s plus a dispatcher mapping tool names back to the
/// corresponding handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-session snapshot.</b> <see cref="SkillRegistry"/> is currently immutable after startup —
/// the host wires it up during boot and never appends. A snapshot at bridge-construction time is
/// therefore enough; we do not subscribe to registry changes. If the registry ever grows a
/// change-notification surface, the bridge can be retrofitted with a re-snapshot call.
/// </para>
/// <para>
/// <b>Schema resolver.</b> A skill's <see cref="SkillManifest"/> has no JSON-Schema slot today
/// (see <c>SkillVoiceTool</c> remarks). Callers that want a per-skill schema override can supply a
/// <see cref="Func{T, TResult}"/> at bridge construction. The default returns <see langword="null"/>,
/// which causes <see cref="SkillVoiceTool"/> to fall back to its permissive string-map schema.
/// </para>
/// <para>
/// <b>No-op when registry is empty.</b> A bridge built around a registry with zero skills produces
/// an empty <see cref="GetToolDefinitions"/> result and never finds a name in <see cref="TryGetHandler"/>.
/// Wiring code can register it unconditionally without special-casing the empty path.
/// </para>
/// </remarks>
public sealed class SkillRegistryVoiceBridge
{
    private readonly Dictionary<string, SkillVoiceTool> _tools;

    /// <summary>Construct a bridge that snapshots the given registry into per-skill adapters.</summary>
    /// <param name="registry">Source registry; iterated once at construction.</param>
    /// <param name="workspace">Workspace handed to every <see cref="SkillContext"/> the bridge builds.</param>
    /// <param name="loggerFactory">Logger factory threaded into each adapter's per-invocation context.</param>
    /// <param name="http">Optional shared HTTP client. Defaults to a bridge-owned <see cref="HttpClient"/>.</param>
    /// <param name="environment">Optional environment override; null = process environment at invocation time.</param>
    /// <param name="schemaResolver">
    /// Optional per-manifest schema resolver. Return a JSON-Schema object to override the default
    /// permissive schema for a given skill; return <see langword="null"/> to keep the default.
    /// </param>
    /// <param name="skillRootResolver">
    /// Optional per-manifest skill-root resolver. Return the on-disk path the skill was loaded from
    /// (used to resolve relative asset paths). Falls back to <see cref="AppContext.BaseDirectory"/>
    /// when the resolver is null or returns null/empty.
    /// </param>
    public SkillRegistryVoiceBridge(
        SkillRegistry registry,
        WorkspaceDirectory workspace,
        ILoggerFactory? loggerFactory = null,
        HttpClient? http = null,
        IReadOnlyDictionary<string, string>? environment = null,
        Func<SkillManifest, JsonElement?>? schemaResolver = null,
        Func<SkillManifest, string?>? skillRootResolver = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(workspace);

        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        var sharedHttp = http ?? new HttpClient();

        _tools = new Dictionary<string, SkillVoiceTool>(StringComparer.Ordinal);
        foreach (var manifest in registry.Manifests)
        {
            var skill = registry.TryGet(manifest.Id);
            if (skill is null)
            {
                // Defensive: a manifest in the registry that fails to resolve as an ISkill is a
                // configuration issue, not a runtime fault. Skip it rather than aborting the host —
                // the operator will see "tool unknown" responses if the model tries to call it.
                continue;
            }
            var schema = schemaResolver?.Invoke(manifest);
            var root = skillRootResolver?.Invoke(manifest);
            if (string.IsNullOrEmpty(root))
            {
                root = AppContext.BaseDirectory;
            }

            // Translate dotted / colon-separated ids (e.g. notes.search) into the underscore-only
            // form Gemini Live requires for tool names. The skill id stays unchanged on the registry
            // side so `sutando skills run notes.search` keeps working; only the wire-side tool name
            // changes. We key _tools by the translated name so dispatch on the inbound wire name
            // resolves correctly.
            string? nameOverride = null;
            var translated = TranslateToGeminiToolName(manifest.Id);
            if (!string.Equals(translated, manifest.Id, StringComparison.Ordinal))
            {
                nameOverride = translated;
            }

            SkillVoiceTool tool;
            try
            {
                tool = new SkillVoiceTool(
                    skill: skill,
                    workspace: workspace,
                    skillRoot: root,
                    loggerFactory: factory,
                    http: sharedHttp,
                    environment: environment,
                    parameterSchemaOverride: schema,
                    toolNameOverride: nameOverride);
            }
            catch (ArgumentException)
            {
                // Skill id still contains characters Gemini rejects even after translation (e.g.
                // spaces). Skip rather than abort — operators with a mix of valid + invalid skills
                // shouldn't lose the whole bridge over one bad id.
                continue;
            }

            // Collision guard: if two skills translate to the same wire name (e.g. notes.search and
            // notes_search both registered), drop the second. Loud failure here would block boot;
            // first-wins keeps the bridge usable.
            _tools.TryAdd(tool.Definition.Name, tool);
        }
    }

    /// <summary>
    /// Translate a Sutando skill id into a Gemini Live-compatible tool name by replacing characters
    /// the skills convention allows but Gemini does not (currently <c>.</c> and <c>:</c>) with
    /// underscores. Other characters are left unchanged so the adapter's strict validator still
    /// surfaces genuinely-invalid ids.
    /// </summary>
    /// <param name="id">The skill manifest id.</param>
    /// <returns>The translated tool name, equal to <paramref name="id"/> when no translation was needed.</returns>
    private static string TranslateToGeminiToolName(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return id;
        }
        if (id.IndexOfAny(['.', ':']) < 0)
        {
            return id;
        }
        return id.Replace('.', '_').Replace(':', '_');
    }

    /// <summary>The number of skills the bridge currently exposes.</summary>
    public int Count => _tools.Count;

    /// <summary>
    /// All registered tool definitions, in the order they were taken off the registry. Suitable
    /// for plugging directly into <see cref="RealtimeSessionConfig.Tools"/>.
    /// </summary>
    /// <returns>The tool definitions snapshot.</returns>
    public IReadOnlyList<RealtimeToolDefinition> GetToolDefinitions()
    {
        var list = new List<RealtimeToolDefinition>(_tools.Count);
        foreach (var tool in _tools.Values)
        {
            list.Add(tool.Definition);
        }
        return list;
    }

    /// <summary>
    /// Look up the handler for a given tool name. Returns <see langword="null"/> when the name was
    /// not registered — matches the contract <see cref="VoiceSession"/> already implements (it
    /// surfaces an <c>{ "error": "Tool '&lt;name&gt;' is not registered." }</c> envelope in that case).
    /// </summary>
    /// <param name="toolName">The tool name as it appears on the wire.</param>
    /// <returns>The matching handler, or null when no skill with that id is registered.</returns>
    public RealtimeToolHandler? TryGetHandler(string toolName)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            return null;
        }
        return _tools.TryGetValue(toolName, out var tool) ? tool.Handler : null;
    }

    /// <summary>Snapshot of every adapter the bridge currently owns. Exposed for tests and diagnostics.</summary>
    public IReadOnlyCollection<SkillVoiceTool> Tools => _tools.Values;

    /// <summary>
    /// Register every adapter against the given session. Each tool is added via
    /// <see cref="VoiceSession.RegisterTool"/> using <see cref="SkillVoiceTool.Definition"/> +
    /// <see cref="SkillVoiceTool.Handler"/>. Should be called before
    /// <see cref="VoiceSession.ConnectAsync"/> so the tool list is advertised on the setup envelope.
    /// </summary>
    /// <param name="session">Target session.</param>
    /// <exception cref="InvalidOperationException">If a tool with the same name is already registered on the session (e.g. a prior bridge or a manually-registered handler).</exception>
    public void RegisterWith(VoiceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        foreach (var tool in _tools.Values)
        {
            session.RegisterTool(tool.Definition, tool.Handler);
        }
    }
}

/// <summary>
/// DI helpers for wiring a <see cref="SkillRegistryVoiceBridge"/> into the voice host. The
/// presence (or absence) of a bridge registration controls whether <see cref="VoiceWebSocketHandler"/>
/// advertises skill-derived tools — when no bridge is in the container, the default voice path is
/// unchanged.
/// </summary>
public static class SkillRegistryVoiceBridgeServiceCollectionExtensions
{
    /// <summary>
    /// Register a <see cref="SkillRegistryVoiceBridge"/> as a singleton built from a configured
    /// <see cref="SkillRegistry"/> and <see cref="WorkspaceDirectory"/> already in the container.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddSkillRegistryVoiceBridge(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<SkillRegistryVoiceBridge>(sp =>
        {
            var registry = sp.GetRequiredService<SkillRegistry>();
            var workspace = sp.GetRequiredService<WorkspaceDirectory>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new SkillRegistryVoiceBridge(registry, workspace, loggerFactory);
        });
        return services;
    }

    /// <summary>
    /// Register a pre-built <see cref="SkillRegistryVoiceBridge"/> instance as a singleton. Useful
    /// when the host wants full control over the bridge's construction (custom schema resolver,
    /// custom HTTP client, etc.).
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <param name="bridge">The pre-built bridge.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddSkillRegistryVoiceBridge(
        this IServiceCollection services,
        SkillRegistryVoiceBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(bridge);
        services.AddSingleton(bridge);
        return services;
    }
}
