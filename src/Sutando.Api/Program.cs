namespace Sutando.Api;

/// <summary>
/// Entry-point shim for <c>dotnet run</c> on this project. Declared as a non-static partial
/// class so the ASP.NET Core test host (<c>WebApplicationFactory&lt;Program&gt;</c>) can
/// target it. Real bring-up lives in <see cref="ApiCommand"/> so the future
/// <c>sutando api</c> CLI verb is a one-liner.
/// </summary>
public partial class Program
{
    /// <summary>Hidden constructor; this type is referenced only via its static <see cref="Main"/>.</summary>
    protected Program() { }

    /// <summary>Process entry point; delegates to <see cref="ApiCommand.RunAsync"/>.</summary>
    /// <param name="args">Process command-line args.</param>
    /// <returns>The process exit task.</returns>
    public static Task Main(string[] args) => ApiCommand.RunAsync(args, CancellationToken.None);
}
