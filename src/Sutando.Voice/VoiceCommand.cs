namespace Sutando.Voice;

/// <summary>
/// Thin CLI shim mirroring <c>BrowserCommand</c>'s shape (in <c>Sutando.Browser</c>). The
/// future <c>sutando voice</c> verb wires its arguments here so the CLI integration stays a
/// one-liner.
/// </summary>
/// <remarks>
/// Hosting note: <see cref="VoiceServer.Build"/> binds Kestrel to <c>http://0.0.0.0:&lt;port&gt;</c>.
/// The verb runs the host in foreground until the cancellation token supplied to
/// <see cref="RunAsync(string[], CancellationToken)"/> is cancelled (Ctrl-C in the CLI process).
/// </remarks>
public static class VoiceCommand
{
    /// <summary>
    /// Run the voice WS server until <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="args">CLI args. Forwarded to <see cref="VoiceServer.Build"/> — recognised flags include <c>--port &lt;n&gt;</c>.</param>
    /// <param name="ct">Cancellation token. Cancelling triggers a graceful host shutdown.</param>
    /// <returns>Process exit code — <c>0</c> on clean shutdown.</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        var app = VoiceServer.Build(args);
        await app.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }
}
