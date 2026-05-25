// Lives in Sutando.Voice (not Sutando.Realtime) because Sutando.Phone also consumes Sutando.Realtime
// and must not be pulled into a Sutando.Skills dependency it does not need.
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Realtime;
using Sutando.Skills;
using Sutando.Workspace;

namespace Sutando.Voice.Skills;

/// <summary>
/// Adapter that projects a single <see cref="ISkill"/> onto the realtime tool-call surface used by
/// <see cref="VoiceSession"/>. Produces a <see cref="RealtimeToolDefinition"/> the model can see,
/// and a <see cref="RealtimeToolHandler"/> delegate that builds a per-invocation
/// <see cref="SkillContext"/>, dispatches into <see cref="ISkill.ExecuteAsync"/>, and shapes the
/// <see cref="SkillResult"/> into the JSON payload the realtime client expects.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tool name.</b> The skill manifest's <see cref="SkillManifest.Id"/> is used verbatim as the
/// tool name. Gemini Live requires names that match <c>[a-zA-Z0-9_-]{1,63}</c>; Sutando skill IDs
/// are lower-kebab-case so the natural ID space is compatible. IDs that include other characters
/// (<c>.</c>, <c>:</c>, …) are rejected at adapter construction with a clear
/// <see cref="ArgumentException"/>.
/// </para>
/// <para>
/// <b>Parameter schema.</b> <see cref="SkillManifest"/> carries no JSON-Schema today. The default
/// schema we hand to Gemini reflects what <see cref="ISkill.ExecuteAsync"/> actually accepts — a
/// free-form string→string map. Callers that want a richer schema per skill can supply an explicit
/// override via the ctor's <c>parameterSchemaOverride</c> parameter (the bridge wires this through
/// its resolver delegate). The follow-up note in <c>INTEGRATION-NOTES.md</c> captures the long-term
/// path: hang a schema slot off <c>SkillManifest</c> when we touch <c>Sutando.Skills</c> next.
/// </para>
/// <para>
/// <b>Argument coercion.</b> Gemini hands us a JSON object; <see cref="ISkill.ExecuteAsync"/> wants
/// <c>IReadOnlyDictionary&lt;string, string&gt;</c>. For each top-level property:
/// <list type="bullet">
///   <item><description>strings flow through as <see cref="JsonElement.GetString"/>.</description></item>
///   <item><description>everything else is serialised back to its raw JSON text via
///   <see cref="JsonElement.GetRawText"/> — skills that want numbers / nested objects can re-parse.</description></item>
/// </list>
/// Top-level non-object arguments (e.g. the model emits a bare string) are dropped to an empty
/// dictionary — same as a no-argument call.
/// </para>
/// </remarks>
public sealed class SkillVoiceTool
{
    /// <summary>The skill this adapter wraps.</summary>
    public ISkill Skill { get; }

    /// <summary>The tool definition advertised to the realtime model.</summary>
    public RealtimeToolDefinition Definition { get; }

    private readonly WorkspaceDirectory _workspace;
    private readonly string _skillRoot;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, string>? _environment;

    /// <summary>Construct an adapter for the given skill.</summary>
    /// <param name="skill">The skill to wrap.</param>
    /// <param name="workspace">Workspace handed to every <see cref="SkillContext"/> the handler builds.</param>
    /// <param name="skillRoot">
    /// Filesystem root for the skill. Typically the directory the skill was discovered from. When
    /// the skill is an assembly-registered built-in (no on-disk root), pass <see cref="AppContext.BaseDirectory"/>.
    /// </param>
    /// <param name="loggerFactory">Logger factory used to build a per-invocation logger.</param>
    /// <param name="http">Shared HTTP client given to every <see cref="SkillContext"/>.</param>
    /// <param name="environment">Environment override; <see langword="null"/> = process environment at invocation time.</param>
    /// <param name="parameterSchemaOverride">
    /// Optional JSON-Schema object describing the tool's parameters. When null, the adapter falls
    /// back to a permissive <c>{"type":"object","additionalProperties":{"type":"string"}}</c>
    /// schema that mirrors what <see cref="ISkill.ExecuteAsync"/> actually accepts.
    /// </param>
    /// <exception cref="ArgumentException">When the skill's id is not a valid Gemini Live tool name.</exception>
    public SkillVoiceTool(
        ISkill skill,
        WorkspaceDirectory workspace,
        string skillRoot,
        ILoggerFactory? loggerFactory = null,
        HttpClient? http = null,
        IReadOnlyDictionary<string, string>? environment = null,
        JsonElement? parameterSchemaOverride = null)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrEmpty(skillRoot);

        Skill = skill;
        _workspace = workspace;
        _skillRoot = skillRoot;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _http = http ?? new HttpClient();
        _environment = environment;

        var name = skill.Manifest.Id;
        ValidateGeminiToolName(name);

        var schema = parameterSchemaOverride ?? BuildDefaultSchema(skill.Manifest.Description);
        var description = string.IsNullOrWhiteSpace(skill.Manifest.Description)
            ? skill.Manifest.Name
            : skill.Manifest.Description;

        Definition = new RealtimeToolDefinition(name, description, schema);
    }

    /// <summary>
    /// The handler delegate to register with <see cref="VoiceSession.RegisterTool"/>. Each call
    /// builds a fresh <see cref="SkillContext"/>, invokes the skill, and projects the
    /// <see cref="SkillResult"/> into a JSON payload the realtime client will forward back to the
    /// model. Errors thrown by the skill are caught and surfaced as a
    /// <c>{ "error": "&lt;message&gt;" }</c> envelope — the bare-throw path is reserved for cancellation.
    /// </summary>
    public RealtimeToolHandler Handler => InvokeAsync;

    private async Task<JsonElement> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var logger = _loggerFactory.CreateLogger($"Sutando.Voice.Skills.{Skill.Manifest.Id}");
        var args = CoerceArguments(arguments);

        var context = new SkillContext(
            workspace: _workspace,
            skillRoot: _skillRoot,
            logger: logger,
            http: _http,
            env: _environment);

        // Exceptions raised by the skill itself are caught by VoiceSession.DispatchToolAsync and
        // turned into a { "error": "..." } envelope. We catch here too so the result projection
        // for a SkillResult.Fail (logical failure, not an exception) stays consistent — the
        // model sees the same shape regardless of how the skill signalled the failure.
        var sw = Stopwatch.StartNew();
        SkillResult result;
        try
        {
            result = await Skill.ExecuteAsync(context, args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(ex, "Skill '{Id}' threw — surfacing to model as error envelope.", Skill.Manifest.Id);
            return JsonSerializer.SerializeToElement(new
            {
                ok = false,
                error = ex.Message,
                duration_ms = sw.Elapsed.TotalMilliseconds,
            });
        }
        sw.Stop();

        if (!result.Success)
        {
            return JsonSerializer.SerializeToElement(new
            {
                ok = false,
                error = result.Error ?? result.Body,
                body = result.Body,
                duration_ms = result.Duration.TotalMilliseconds,
                artifacts = result.Artifacts,
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            ok = true,
            body = result.Body,
            duration_ms = result.Duration.TotalMilliseconds,
            artifacts = result.Artifacts,
        });
    }

    /// <summary>
    /// Coerces a Gemini-supplied JSON-object argument payload into the string→string shape the
    /// <see cref="ISkill"/> contract accepts. See class-level remarks for the per-kind rules.
    /// </summary>
    /// <param name="arguments">The arguments JSON element handed in by the realtime session.</param>
    /// <returns>A flat string→string dictionary. Empty when <paramref name="arguments"/> is not a JSON object.</returns>
    internal static IReadOnlyDictionary<string, string> CoerceArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            // Strings flow through as their character value; non-strings survive as raw JSON so
            // skills that want numbers / nested data can re-parse. Null becomes the literal "null".
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Null => "null",
                _ => property.Value.GetRawText(),
            };
            dict[property.Name] = value;
        }
        return dict;
    }

    /// <summary>
    /// Permissive default schema — mirrors what <see cref="ISkill.ExecuteAsync"/> actually accepts
    /// (a free-form string→string map). The description text is the skill's own description so the
    /// model has something useful to read during function-selection.
    /// </summary>
    /// <param name="description">The skill's description, surfaced into the schema's description.</param>
    private static JsonElement BuildDefaultSchema(string description)
    {
        var safe = string.IsNullOrWhiteSpace(description) ? "Skill arguments — free-form key/value pairs." : description;
        var json = JsonSerializer.Serialize(new
        {
            type = "object",
            description = safe,
            additionalProperties = new { type = "string" },
        });
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static void ValidateGeminiToolName(string name)
    {
        // Gemini Live: name must match [a-zA-Z0-9_-], length 1..63.
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Skill id is empty — cannot project as a realtime tool name.", nameof(name));
        }
        if (name.Length > 63)
        {
            throw new ArgumentException(
                $"Skill id '{name}' exceeds the 63-character Gemini Live tool-name limit.", nameof(name));
        }
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var ok = (c >= 'a' && c <= 'z')
                  || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9')
                  || c == '_'
                  || c == '-';
            if (!ok)
            {
                throw new ArgumentException(
                    $"Skill id '{name}' contains character '{c}' at position {i} — Gemini Live tool names allow only [a-zA-Z0-9_-].",
                    nameof(name));
            }
        }
    }
}
