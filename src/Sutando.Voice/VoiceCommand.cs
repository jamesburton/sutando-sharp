using Sutando.Voice.Skills;

namespace Sutando.Voice;

/// <summary>
/// Thin CLI shim mirroring <c>BrowserCommand</c>'s shape (in <c>Sutando.Browser</c>). The
/// <c>sutando voice</c> verb wires its arguments here so the CLI integration stays a one-liner.
/// </summary>
/// <remarks>
/// Hosting note: <see cref="VoiceServer.Build(string[], SkillRegistryVoiceBridge?)"/> binds
/// Kestrel to <c>http://0.0.0.0:&lt;port&gt;</c>. The verb runs the host in foreground until the
/// cancellation token supplied to <see cref="RunAsync(string[], SkillRegistryVoiceBridge?, CancellationToken)"/>
/// is cancelled (Ctrl-C in the CLI process).
/// </remarks>
public static class VoiceCommand
{
    /// <summary>
    /// Run the voice WS server until <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="args">CLI args. Forwarded to <see cref="VoiceServer.Build(string[], SkillRegistryVoiceBridge?)"/> — recognised flags include <c>--port &lt;n&gt;</c>.</param>
    /// <param name="skillBridge">Optional skill bridge — when supplied, the voice host advertises every skill in the bridge as a callable tool. Null = plain voice with no skill tools.</param>
    /// <param name="ct">Cancellation token. Cancelling triggers a graceful host shutdown.</param>
    /// <returns>Process exit code — <c>0</c> on clean shutdown.</returns>
    public static async Task<int> RunAsync(string[] args, SkillRegistryVoiceBridge? skillBridge = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        var app = VoiceServer.Build(args, skillBridge);
        await app.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Back-compat overload — for callers that don't want skill-tool integration. Delegates to the
    /// bridge-aware overload with a null bridge.
    /// </summary>
    /// <param name="args">CLI args.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Process exit code — <c>0</c> on clean shutdown.</returns>
    public static Task<int> RunAsync(string[] args, CancellationToken ct)
        => RunAsync(args, skillBridge: null, ct);
}
