namespace Sutando.Phone;

/// <summary>
/// Thin CLI shim mirroring <c>VoiceCommand</c>'s shape (in <c>Sutando.Voice</c>). The
/// <c>sutando phone</c> verb wires its arguments here so the CLI integration stays a one-liner.
/// </summary>
/// <remarks>
/// Hosting note: <see cref="PhoneServer.Build(string[], PhoneSkillBridge?)"/> binds Kestrel to
/// <c>http://0.0.0.0:&lt;port&gt;</c>. The verb runs the host in foreground until the cancellation
/// token supplied to <see cref="RunAsync(string[], PhoneSkillBridge?, CancellationToken)"/> is
/// cancelled (Ctrl-C in the CLI process).
/// </remarks>
public static class PhoneCommand
{
    /// <summary>
    /// Run the Twilio phone bridge until <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="args">CLI args. Forwarded to <see cref="PhoneServer.Build(string[], PhoneSkillBridge?)"/> — recognised flags include <c>--port &lt;n&gt;</c>.</param>
    /// <param name="skillBridge">Optional skill bridge — when supplied, every call advertises every skill in the bridge as a callable tool (matches upstream conversation-server.ts:587).</param>
    /// <param name="ct">Cancellation token. Cancelling triggers a graceful host shutdown.</param>
    /// <returns>Process exit code — <c>0</c> on clean shutdown.</returns>
    public static async Task<int> RunAsync(string[] args, PhoneSkillBridge? skillBridge = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        var app = PhoneServer.Build(args, skillBridge);
        await app.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Back-compat overload — for callers that don't want skill-tool integration.</summary>
    /// <param name="args">CLI args.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Process exit code — <c>0</c> on clean shutdown.</returns>
    public static Task<int> RunAsync(string[] args, CancellationToken ct)
        => RunAsync(args, skillBridge: null, ct);
}
