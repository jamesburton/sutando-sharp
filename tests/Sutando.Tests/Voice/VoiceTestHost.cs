using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sutando.Realtime;
using Sutando.Tests.Realtime;
using Sutando.Voice;

namespace Sutando.Tests.Voice;

/// <summary>
/// In-process host for the voice WS server, with the live Gemini transport substituted by an
/// in-process <see cref="FakeRealtimeClient"/>. Tests pump events into the fake session via
/// <see cref="FakeFactory.LatestSession"/>.
/// </summary>
internal sealed class VoiceTestHost : WebApplicationFactory<Program>
{
    public FakeFactory Factory { get; } = new();

    public VoiceTestHost()
    {
        // The host refuses /voice upgrades when no API key is set; tests need a non-empty value so
        // the WS handshake reaches our fake transport instead of bouncing with 1008.
        Environment.SetEnvironmentVariable("GEMINI_VOICE_API_KEY", "test-key");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IRealtimeTransportFactory>();
            services.AddSingleton<IRealtimeTransportFactory>(Factory);
        });
    }

    /// <summary>
    /// Fake realtime-client factory. Captures every client it hands out so tests can drive
    /// events through the session that the WS handler creates.
    /// </summary>
    internal sealed class FakeFactory : IRealtimeTransportFactory
    {
        private readonly object _gate = new();
        private FakeRealtimeClient? _latest;
        private TaskCompletionSource<FakeRealtimeClient> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The most recently allocated fake client.</summary>
        public FakeRealtimeClient? LatestClient
        {
            get { lock (_gate) { return _latest; } }
        }

        /// <summary>The session minted on the most recent client. Null until the handler calls <c>CreateSessionAsync</c>.</summary>
        public FakeRealtimeClientSession? LatestSession => LatestClient?.LatestSession;

        /// <summary>Waits up to <paramref name="timeout"/> for the next <see cref="Create"/> call AND the corresponding session creation.</summary>
        public async Task<FakeRealtimeClientSession> WaitForCreateAsync(TimeSpan timeout)
        {
            Task<FakeRealtimeClient> task;
            lock (_gate)
            {
                if (_latest is not null && _latest.LatestSession is not null)
                {
                    return _latest.LatestSession;
                }
                task = _next.Task;
            }
            var client = await task.WaitAsync(timeout).ConfigureAwait(false);

            // CreateSessionAsync resolves synchronously inside the handler, but the read-loop may
            // not have started yet — poll briefly until LatestSession appears.
            var deadline = DateTime.UtcNow + timeout;
            while (client.LatestSession is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }
            return client.LatestSession
                ?? throw new TimeoutException("Fake client created but no session was minted within the timeout.");
        }

        public IRealtimeClient Create()
        {
            var client = new FakeRealtimeClient();
            TaskCompletionSource<FakeRealtimeClient> previous;
            lock (_gate)
            {
                _latest = client;
                previous = _next;
                _next = new TaskCompletionSource<FakeRealtimeClient>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            previous.TrySetResult(client);
            return client;
        }
    }
}

/// <summary>Helper extension to mirror Microsoft.Extensions.DependencyInjection.Extensions.RemoveAll without pulling the package.</summary>
internal static class ServiceCollectionExtensions
{
    public static IServiceCollection RemoveAll<TService>(this IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(TService))
            {
                services.RemoveAt(i);
            }
        }
        return services;
    }
}
