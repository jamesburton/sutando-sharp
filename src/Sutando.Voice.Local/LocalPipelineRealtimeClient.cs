using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sutando.Voice.Local;

/// <summary>
/// A MEAI <see cref="IRealtimeClient"/> that mints <see cref="LocalPipelineRealtimeClientSession"/>s
/// — the <c>sutando voice --local</c> transport. Each session runs one voice connection through
/// the in-process STT → Chat → TTS pipeline instead of a cloud realtime API.
/// </summary>
/// <remarks>
/// <para>
/// Slots in wherever <c>GeminiLiveRealtimeClient</c> would: <c>Sutando.Voice</c>'s
/// <c>IRealtimeTransportFactory</c> returns one of these instead, and the rest of the voice WS
/// server is untouched. The split (reusable client owning shared model handles; per-connection
/// session owning one pipeline run) mirrors MEAI's client/session boundary exactly.
/// </para>
/// <para>
/// The four pluggable stage components live on <see cref="LocalPipelineOptions"/> and are reused
/// across every session this client creates — model weights load once, not per connection.
/// </para>
/// </remarks>
public sealed class LocalPipelineRealtimeClient : IRealtimeClient
{
    private readonly LocalPipelineOptions? _options;
    private readonly string? _configurationError;
    private readonly ILoggerFactory _loggerFactory;
    private bool _disposed;

    /// <summary>Create a local-pipeline realtime client.</summary>
    /// <param name="options">The four stage components plus tuning knobs, shared across every session.</param>
    /// <param name="loggerFactory">Logger factory passed to each session. Defaults to <see cref="NullLoggerFactory.Instance"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public LocalPipelineRealtimeClient(LocalPipelineOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// Create a client whose sessions report a configuration error instead of running a
    /// pipeline. Used by <c>Sutando.Voice</c> when <c>--local</c> is requested but the
    /// local-inference model files could not be resolved at server boot. Every session
    /// completes the handshake then surfaces <paramref name="configurationError"/> to the
    /// browser so the operator sees exactly what is missing.
    /// </summary>
    /// <param name="configurationError">Human-readable explanation of what is missing.</param>
    /// <param name="loggerFactory">Logger factory passed to each session.</param>
    /// <exception cref="ArgumentException"><paramref name="configurationError"/> is null / empty.</exception>
    public static LocalPipelineRealtimeClient Unavailable(string configurationError, ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationError);
        return new LocalPipelineRealtimeClient(configurationError, loggerFactory);
    }

    private LocalPipelineRealtimeClient(string configurationError, ILoggerFactory? loggerFactory)
    {
        _configurationError = configurationError;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <inheritdoc/>
    public Task<IRealtimeClientSession> CreateSessionAsync(
        RealtimeSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Session creation is cheap and synchronous — the pipeline is built and run lazily on
        // the first SendAsync / GetStreamingResponseAsync, matching GeminiLiveRealtimeClientSession.
        var logger = _loggerFactory.CreateLogger<LocalPipelineRealtimeClientSession>();
        var session = _configurationError is { } error
            ? new LocalPipelineRealtimeClientSession(error, options, logger)
            : new LocalPipelineRealtimeClientSession(_options!, options, logger);

        return Task.FromResult<IRealtimeClientSession>(session);
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsAssignableFrom(GetType()))
        {
            return this;
        }
        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // The four stage components are caller-owned (DI singletons reused across voice sessions);
        // disposing them here would tear out the model weights from under live connections.
        _disposed = true;
    }
}
