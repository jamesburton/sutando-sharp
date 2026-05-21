namespace Sutando.Tests.Platform.Windows;

/// <summary>
/// Serializes test classes that perform read/write operations against the Windows system clipboard.
/// </summary>
/// <remarks>
/// The Windows clipboard is a single shared OS-global resource. Any test that calls
/// <c>SetText</c> or <c>GetText</c> on it must belong to this collection so that clipboard
/// round-trips cannot be clobbered by a concurrently-running test (or test-host process)
/// touching the same resource. Members of this collection run serially across the whole
/// test assembly.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsClipboardCollection
{
    /// <summary>The xUnit collection name. Apply with <c>[Collection(WindowsClipboardCollection.Name)]</c>.</summary>
    public const string Name = "Windows clipboard";
}
