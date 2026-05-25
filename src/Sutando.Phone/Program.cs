namespace Sutando.Phone;

/// <summary>
/// Entry-point shim for <c>dotnet run</c> on this project. Declared as a non-static partial
/// class so the ASP.NET Core test host (<c>WebApplicationFactory&lt;Program&gt;</c>) can
/// target it. Real bring-up lives in <see cref="PhoneServer.Build"/> so the
/// <c>sutando phone</c> CLI verb stays a one-liner.
/// </summary>
/// <remarks>
/// Top-level statements were considered but rejected: they emit a synthetic <c>Program</c>
/// in the <i>global</i> namespace, which collides with the <c>Program</c> in
/// <c>Sutando.Api</c>, <c>Sutando.Dashboard</c>, and <c>Sutando.Voice</c> at runtime. Test
/// runs end up routing <c>WebApplicationFactory&lt;Program&gt;</c> requests to the wrong
/// host. Explicit namespacing eliminates the ambiguity.
/// </remarks>
public partial class Program
{
    /// <summary>Hidden constructor; this type is referenced only via its static <see cref="Main"/>.</summary>
    protected Program() { }

    /// <summary>Process entry point; delegates to <see cref="PhoneCommand.RunAsync(string[], CancellationToken)"/>.</summary>
    /// <param name="args">Process command-line args.</param>
    /// <returns>Process exit task.</returns>
    public static async Task Main(string[] args)
    {
        await PhoneCommand.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
    }
}
