namespace Sutando.Tests.Api;

/// <summary>
/// Serializes test classes that mutate the per-process <c>SUTANDO_WORKSPACE</c> env var or
/// rely on <see cref="Sutando.Workspace.WorkspaceDirectory.Resolve(Microsoft.Extensions.Logging.ILogger{Sutando.Workspace.WorkspaceDirectory}?, string?)"/>.
/// </summary>
/// <remarks>
/// The Api and Dashboard DI factories both call <c>Environment.SetEnvironmentVariable</c>
/// when a <c>WorkspaceRoot</c> override is configured. xUnit parallelizes tests across
/// classes by default, which would race on that mutation. Members of this collection run
/// serially across the whole test assembly.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkspaceCollection
{
    /// <summary>The xUnit collection name. Apply with <c>[Collection(WorkspaceCollection.Name)]</c>.</summary>
    public const string Name = "Workspace";
}
