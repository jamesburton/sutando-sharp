using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Workspace;

namespace Sutando.Skills;

/// <summary>
/// Execution context handed to <see cref="ISkill.ExecuteAsync"/>. Carries the workspace,
/// logger, and supporting services that skills need to do their work.
/// </summary>
public sealed class SkillContext
{
    /// <summary>The resolved workspace directory.</summary>
    public WorkspaceDirectory Workspace { get; }

    /// <summary>Filesystem root the skill was loaded from — useful for resolving relative asset paths.</summary>
    public string SkillRoot { get; }

    /// <summary>Logger scoped to this skill invocation.</summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Shared HTTP client. Skills should use this rather than constructing their own
    /// <see cref="HttpClient"/>s — pools and timeouts are configured once per host.
    /// </summary>
    public HttpClient Http { get; }

    /// <summary>Environment variables visible to the skill. Defaults to <see cref="Environment.GetEnvironmentVariables()"/>.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    /// <param name="workspace">Resolved workspace.</param>
    /// <param name="skillRoot">Filesystem root of the skill being executed.</param>
    /// <param name="logger">Per-invocation logger.</param>
    /// <param name="http">Shared HTTP client.</param>
    /// <param name="env">Optional explicit env override; defaults to the process environment.</param>
    public SkillContext(
        WorkspaceDirectory workspace,
        string skillRoot,
        ILogger? logger = null,
        HttpClient? http = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        Workspace = workspace;
        SkillRoot = skillRoot;
        Logger = logger ?? NullLogger.Instance;
        Http = http ?? new HttpClient();
        Environment = env ?? CurrentEnvironment();
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
