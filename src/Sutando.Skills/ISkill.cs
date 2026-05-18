namespace Sutando.Skills;

/// <summary>
/// A skill — a discrete capability that the agent (or other skills) can invoke.
/// </summary>
/// <remarks>
/// Concrete skills come from three sources:
/// <list type="bullet">
///   <item><description>
///     Managed: a <see cref="SkillRuntime.Managed"/> manifest pointing at a CLR type that
///     implements this interface. Loaded via reflection from a registered assembly.
///   </description></item>
///   <item><description>
///     Scripted: a Python / Node / Bash script invoked via subprocess by the corresponding
///     runtime. The script reads JSON arguments from stdin and writes a JSON result to stdout.
///   </description></item>
///   <item><description>
///     dotnet tool: a NuGet tool resolved via <c>dotnet tool run</c>. Subprocess like scripts
///     but resolved from the package cache.
///   </description></item>
/// </list>
/// The runtime difference is hidden by <see cref="ISkill"/>: callers always invoke
/// <see cref="ExecuteAsync"/> and receive a <see cref="SkillResult"/>.
/// </remarks>
public interface ISkill
{
    /// <summary>The manifest this skill was loaded from.</summary>
    SkillManifest Manifest { get; }

    /// <summary>Execute the skill with the given arguments.</summary>
    /// <param name="context">Workspace + logger + http handles.</param>
    /// <param name="arguments">Free-form skill arguments. Schema is per-skill.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SkillResult> ExecuteAsync(
        SkillContext context,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken ct);
}
