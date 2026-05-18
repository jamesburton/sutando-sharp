using System.Diagnostics;
using System.Text;

namespace Sutando.Skills.Builtin;

/// <summary>
/// Reference managed skill — echoes its arguments back as a result body. Doubles as the
/// canonical example of writing a managed skill against the <see cref="ISkill"/> interface
/// and as a smoke test for the runtime.
/// </summary>
public sealed class EchoSkill : ISkill
{
    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <summary>Construct with the default echo manifest.</summary>
    public EchoSkill() : this(DefaultManifest()) { }

    /// <summary>Construct with a caller-supplied manifest (used by the managed-factory path).</summary>
    public EchoSkill(SkillManifest manifest) => Manifest = manifest;

    /// <inheritdoc/>
    public Task<SkillResult> ExecuteAsync(
        SkillContext context,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        sb.Append("echo: ");
        var first = true;
        foreach (var (k, v) in arguments)
        {
            if (!first) { sb.Append(' '); }
            sb.Append(k).Append('=').Append(v);
            first = false;
        }
        if (first)
        {
            sb.Append("(no arguments)");
        }
        sw.Stop();
        return Task.FromResult(SkillResult.Ok(sb.ToString(), sw.Elapsed));
    }

    /// <summary>The canonical echo manifest, used by both built-in and on-disk variants.</summary>
    public static SkillManifest DefaultManifest() => new()
    {
        Id = "echo",
        Name = "Echo",
        Description = "Echo the supplied arguments back as a result. Reference skill — useful as a smoke test.",
        Version = "1.0.0",
        Runtime = SkillRuntime.Managed,
        Entry = "Sutando.Skills.Builtin.EchoSkill, Sutando.Skills",
        Triggers = ["echo", "ping"],
        Capabilities = [],
    };
}
