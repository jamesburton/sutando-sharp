using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sutando.Skills;

/// <summary>
/// Skill manifest — the structured metadata describing one skill on disk.
/// </summary>
/// <remarks>
/// <para>
/// Wire format is JSON. A manifest typically lives at <c>&lt;skill-root&gt;/skill.json</c>;
/// the directory it lives in defines the skill's filesystem root (used to resolve relative
/// <see cref="Entry"/> paths for script runtimes).
/// </para>
/// <para>
/// Schema is intentionally small. Capabilities, triggers, and parameters are free-form lists
/// for now — once we have a handful of real skills we'll harden the vocabulary.
/// </para>
/// </remarks>
public sealed record SkillManifest
{
    /// <summary>Stable identifier — lower-kebab-case, unique per workspace.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>Human-readable display name.</summary>
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>One-sentence description shown in tool listings.</summary>
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;

    /// <summary>SemVer string.</summary>
    [JsonPropertyName("version")] public string Version { get; init; } = "0.1.0";

    /// <summary>Execution runtime — <see cref="SkillRuntime"/>.</summary>
    [JsonPropertyName("runtime")] public SkillRuntime Runtime { get; init; } = SkillRuntime.Managed;

    /// <summary>
    /// Entry-point reference. Semantics depend on <see cref="Runtime"/>:
    /// <list type="bullet">
    ///   <item><description><see cref="SkillRuntime.Managed"/>: <c>Namespace.Type, Assembly</c> for reflection-based load, or a relative path to a managed DLL (future).</description></item>
    ///   <item><description><see cref="SkillRuntime.Python"/>, <see cref="SkillRuntime.Node"/>, <see cref="SkillRuntime.Bash"/>: script path relative to the skill root.</description></item>
    ///   <item><description><see cref="SkillRuntime.DotnetTool"/>: NuGet tool package id (e.g. <c>Sutando.Skill.Pkg</c>).</description></item>
    /// </list>
    /// </summary>
    [JsonPropertyName("entry")] public required string Entry { get; init; }

    /// <summary>Free-form trigger keywords used to route requests to this skill.</summary>
    [JsonPropertyName("triggers")] public IReadOnlyList<string> Triggers { get; init; } = [];

    /// <summary>Capability hints (<c>http-out</c>, <c>fs-write</c>, <c>screen</c>, <c>microphone</c>, <c>network-in</c>, …).</summary>
    [JsonPropertyName("capabilities")] public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Author / origin metadata (free-form).</summary>
    [JsonPropertyName("author")] public string? Author { get; init; }

    /// <summary>License identifier (SPDX preferred).</summary>
    [JsonPropertyName("license")] public string? License { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Parse a manifest from JSON text.</summary>
    public static SkillManifest Parse(string json)
    {
        SkillManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SkillManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Surface deserialization failures (missing required props, malformed JSON) as
            // FormatException so call sites have a single exception type to catch.
            throw new FormatException($"skill manifest: {ex.Message}", ex);
        }
        if (manifest is null)
        {
            throw new FormatException("skill manifest: empty document");
        }
        Validate(manifest);
        return manifest;
    }

    /// <summary>Read and parse a manifest from a <c>skill.json</c> file.</summary>
    public static SkillManifest ParseFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>Serialise the manifest back to canonical JSON. Round-trips with <see cref="Parse"/>.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    private static void Validate(SkillManifest m)
    {
        if (string.IsNullOrWhiteSpace(m.Id))
        {
            throw new FormatException("skill manifest: 'id' is required and non-empty");
        }
        if (string.IsNullOrWhiteSpace(m.Name))
        {
            throw new FormatException("skill manifest: 'name' is required and non-empty");
        }
        if (string.IsNullOrWhiteSpace(m.Entry))
        {
            throw new FormatException($"skill manifest '{m.Id}': 'entry' is required and non-empty");
        }
    }
}

/// <summary>Skill execution runtime.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SkillRuntime>))]
public enum SkillRuntime
{
    /// <summary>Compiled .NET type discovered via reflection.</summary>
    Managed = 0,

    /// <summary>Python script invoked via the host's <c>python3</c> binary.</summary>
    Python,

    /// <summary>Node.js script invoked via the host's <c>node</c> binary.</summary>
    Node,

    /// <summary>POSIX shell script (<c>bash</c> on Linux/Mac, Git Bash / WSL on Windows).</summary>
    Bash,

    /// <summary>External dotnet tool resolved via <c>dotnet tool run</c>.</summary>
    DotnetTool,
}
