using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sutando.Realtime;

/// <summary>
/// Gemini Live implementation of MEAI's <see cref="IRealtimeClient"/>. Holds connection
/// credentials and per-session defaults; <see cref="CreateSessionAsync"/> mints a fresh
/// <see cref="GeminiLiveRealtimeClientSession"/> per call.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the retired <c>IRealtimeTransport</c> + <c>GeminiLiveTransport</c> pair. The split
/// mirrors MEAI's client/session boundary: clients are reusable and own credentials, sessions
/// own a single WebSocket. See <c>MAPPING.md</c> for the type-by-type translation rationale.
/// </para>
/// <para>
/// <b>Auth:</b> the constructor's <c>apiKey</c> is the default. Callers may override
/// per session by setting <see cref="RealtimeSessionConfig.ApiKey"/> on the per-session config
/// supplied via <see cref="RealtimeSessionOptions.RawRepresentationFactory"/>.
/// </para>
/// </remarks>
public sealed class GeminiLiveRealtimeClient : IRealtimeClient
{
    private readonly string _defaultApiKey;
    private readonly string? _defaultModel;
    private readonly ILoggerFactory _loggerFactory;
    private bool _disposed;

    /// <summary>Creates a new Gemini Live client.</summary>
    /// <param name="apiKey">
    /// Google AI Studio API key. Used as the default for every session; individual sessions may
    /// override via <see cref="RealtimeSessionConfig.ApiKey"/>. Pass <see cref="string.Empty"/>
    /// (or <see langword="null"/>) when every session is expected to supply its own — the client
    /// will fail fast at session-creation time if neither source has a key.
    /// </param>
    /// <param name="defaultModel">
    /// Default model id when neither <see cref="RealtimeSessionOptions.Model"/> nor
    /// <see cref="RealtimeSessionConfig.Model"/> is supplied. Optional.
    /// </param>
    /// <param name="loggerFactory">
    /// Logger factory passed to each created session. Defaults to <see cref="NullLoggerFactory.Instance"/>.
    /// </param>
    public GeminiLiveRealtimeClient(string? apiKey = null, string? defaultModel = null, ILoggerFactory? loggerFactory = null)
    {
        _defaultApiKey = apiKey ?? string.Empty;
        _defaultModel = defaultModel;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <inheritdoc />
    public Task<IRealtimeClientSession> CreateSessionAsync(
        RealtimeSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Pull the Sutando-side config (with Gemini extensions) off the options' raw-repr factory,
        // when present. Greenfield callers that supply only the MEAI options get reasonable
        // defaults; existing Sutando.Voice / Sutando.Phone consumers go through VoiceSession,
        // which always sets the raw-repr factory to the live RealtimeSessionConfig.
        var sutandoConfig = options?.RawRepresentationFactory?.Invoke() as RealtimeSessionConfig;

        var effectiveModel = sutandoConfig?.Model ?? options?.Model ?? _defaultModel
            ?? throw new InvalidOperationException(
                "No Gemini model id supplied. Set RealtimeSessionOptions.Model, RealtimeSessionConfig.Model, or the client's defaultModel.");

        var effectiveKey = !string.IsNullOrEmpty(sutandoConfig?.ApiKey) ? sutandoConfig!.ApiKey! : _defaultApiKey;
        if (string.IsNullOrEmpty(effectiveKey))
        {
            throw new InvalidOperationException(
                "No Gemini API key supplied. Provide one to the client ctor, or set RealtimeSessionConfig.ApiKey per session.");
        }

        // Re-hydrate a Sutando-side config when the caller went through pure MEAI types. The
        // GeminiLiveRealtimeClientSession only needs the Gemini-specific knobs; everything else
        // (voice, system instruction, tools) lives equivalently in either shape.
        var effectiveConfig = sutandoConfig ?? new RealtimeSessionConfig(
            Model: effectiveModel,
            ApiKey: effectiveKey,
            VoiceName: options?.Voice,
            SystemInstruction: options?.Instructions);

        // GeminiLiveRealtimeClientSession does its WebSocket connect inside ConnectAsync — not in
        // its ctor — so creation is cheap and synchronous. The returned task completes with the
        // session ready to be connected (caller pulls it through GetStreamingResponseAsync, which
        // performs the actual handshake on first enumeration).
        var session = new GeminiLiveRealtimeClientSession(
            options ?? new RealtimeSessionOptions { Model = effectiveModel, Voice = effectiveConfig.VoiceName, Instructions = effectiveConfig.SystemInstruction },
            effectiveConfig,
            effectiveKey,
            _loggerFactory.CreateLogger<GeminiLiveRealtimeClientSession>());

        return Task.FromResult<IRealtimeClientSession>(session);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsAssignableFrom(GetType()))
        {
            return this;
        }
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
    }
}
