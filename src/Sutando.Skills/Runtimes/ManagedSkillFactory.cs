using System.Reflection;
using Microsoft.Extensions.Logging;
using Sutando.Skills.Discovery;

namespace Sutando.Skills.Runtimes;

/// <summary>
/// Builds <see cref="ISkill"/> instances from <see cref="SkillRuntime.Managed"/> manifests.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="SkillManifest.Entry"/> string is interpreted as a
/// <see cref="System.Type.AssemblyQualifiedName"/>-style reference,
/// for example <c>Sutando.Skills.Builtin.EchoSkill, Sutando.Skills.Builtin</c>. The factory
/// first resolves the type from the assemblies already loaded into the process, then falls
/// back to <see cref="Assembly.Load(string)"/> on the assembly half of the reference.
/// </para>
/// <para>
/// Plugin-style <see cref="System.Runtime.Loader.AssemblyLoadContext"/> loading of skill DLLs
/// living next to <c>skill.json</c> is a follow-up. The contract here is forward-compatible:
/// when we add it, only the resolution step changes.
/// </para>
/// </remarks>
public sealed class ManagedSkillFactory
{
    private readonly ILogger<ManagedSkillFactory>? _logger;

    /// <param name="logger">Optional logger for diagnostic output.</param>
    public ManagedSkillFactory(ILogger<ManagedSkillFactory>? logger = null) => _logger = logger;

    /// <summary>Instantiate the skill described by <paramref name="discovered"/>.</summary>
    /// <exception cref="InvalidOperationException">The manifest is not <see cref="SkillRuntime.Managed"/> or the type can't be resolved / instantiated.</exception>
    public ISkill Create(DiscoveredSkill discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        if (discovered.Manifest.Runtime != SkillRuntime.Managed)
        {
            throw new InvalidOperationException(
                $"ManagedSkillFactory: manifest '{discovered.Manifest.Id}' has runtime '{discovered.Manifest.Runtime}', expected 'managed'.");
        }

        var type = ResolveType(discovered.Manifest.Entry)
            ?? throw new InvalidOperationException(
                $"managed skill '{discovered.Manifest.Id}': cannot resolve type '{discovered.Manifest.Entry}'.");

        if (!typeof(ISkill).IsAssignableFrom(type))
        {
            throw new InvalidOperationException(
                $"managed skill '{discovered.Manifest.Id}': type '{type.FullName}' does not implement ISkill.");
        }

        // Prefer a constructor taking the manifest so skills can store it. Fall back to parameterless.
        var manifestCtor = type.GetConstructor([typeof(SkillManifest)]);
        if (manifestCtor is not null)
        {
            return (ISkill)manifestCtor.Invoke([discovered.Manifest]);
        }

        var defaultCtor = type.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                $"managed skill '{discovered.Manifest.Id}': type '{type.FullName}' has no SkillManifest or parameterless constructor.");
        return (ISkill)defaultCtor.Invoke(null);
    }

    private Type? ResolveType(string typeReference)
    {
        // First try the fast path — the reference is already fully qualified and the assembly is loaded.
        var direct = Type.GetType(typeReference, throwOnError: false);
        if (direct is not null)
        {
            return direct;
        }

        // If the reference includes ", AssemblyName" but the assembly isn't loaded, load it.
        var commaIdx = typeReference.IndexOf(',', StringComparison.Ordinal);
        if (commaIdx < 0)
        {
            return null;
        }
        var assemblyName = typeReference[(commaIdx + 1)..].Trim();
        if (string.IsNullOrEmpty(assemblyName))
        {
            return null;
        }

        try
        {
            _ = Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or FileLoadException)
        {
            _logger?.LogDebug(ex, "Could not load assembly '{Assembly}' for managed skill resolution", assemblyName);
            return null;
        }

        return Type.GetType(typeReference, throwOnError: false);
    }
}
