using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Sutando.Realtime;
using Sutando.Voice.Local;

namespace Sutando.Voice;

/// <summary>
/// <see cref="IRealtimeTransportFactory"/> for <c>sutando voice --local</c>: hands the voice WS
/// server a <see cref="LocalPipelineRealtimeClient"/> so each connection runs through the
/// in-process STT → Chat → TTS pipeline instead of Gemini Live.
/// </summary>
/// <remarks>
/// <para>
/// Registered in <see cref="VoiceServer.Build"/> in place of <see cref="GeminiLiveTransportFactory"/>
/// when <see cref="VoiceOptions.UseLocal"/> is set. The rest of the voice host —
/// <see cref="VoiceWebSocketHandler"/>, the wire envelopes, <c>/healthz</c> — is unchanged: it
/// only ever sees an <see cref="IRealtimeTransportFactory"/>.
/// </para>
/// <para>
/// <b>The two flavours.</b> This factory builds the "pure in-process" flavour — Whisper.net /
/// KokoroSharp / LlamaSharp / Silero, all loaded from local model files. The
/// "AppHost-orchestrated" flavour (HTTP clients pointed at a GPU box on the LAN) plugs in at
/// exactly the same seam: construct a <see cref="LocalPipelineRealtimeClient"/> with a
/// <see cref="LocalPipelineOptions"/> whose four components come from
/// <c>Sutando.LocalInference.OpenAI</c> instead. See <c>INTEGRATION-NOTES.md</c> for the
/// deferred wiring.
/// </para>
/// <para>
/// <b>Fail-graceful.</b> The factory is built once at server boot. If the local-inference model
/// files cannot be resolved, it captures the error and every <see cref="Create"/> returns a
/// <see cref="LocalPipelineRealtimeClient.Unavailable(string, ILoggerFactory)"/> client — the
/// browser completes the WS handshake then receives a clear <c>error</c> envelope, mirroring the
/// missing-API-key path for Gemini mode. The host still binds; only voice sessions fail.
/// </para>
/// </remarks>
public sealed class LocalPipelineTransportFactory : IRealtimeTransportFactory
{
    private readonly LocalPipelineOptions? _options;
    private readonly string? _configurationError;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Build a factory by resolving the local-inference stack from <paramref name="voiceOptions"/>.
    /// Never throws — a resolution failure is captured and surfaced per-session instead.
    /// </summary>
    /// <param name="voiceOptions">The bound voice options (carries <see cref="VoiceOptions.LocalModels"/>).</param>
    /// <param name="loggerFactory">Logger factory threaded into each created client / session.</param>
    public LocalPipelineTransportFactory(VoiceOptions voiceOptions, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(voiceOptions);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;

        try
        {
            _options = LocalPipelineBootstrap.Build(voiceOptions, loggerFactory);
        }
        catch (LocalPipelineConfigurationException ex)
        {
            // Expected, operator-actionable failure (missing model file). Capture the message;
            // do not let it crash the host — /healthz and the harness page must stay up.
            _configurationError = ex.Message;
            loggerFactory.CreateLogger<LocalPipelineTransportFactory>()
                .LogWarning("Local voice transport unavailable: {Error}", ex.Message);
        }
    }

    /// <inheritdoc/>
    public IRealtimeClient Create() => _options is { } options
        ? new LocalPipelineRealtimeClient(options, _loggerFactory)
        : LocalPipelineRealtimeClient.Unavailable(
            _configurationError ?? "Local voice transport is not configured.",
            _loggerFactory);
}
