using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sutando.Realtime;
using Sutando.Tests.Realtime;
using Sutando.Voice;

namespace Sutando.Tests.Voice;

/// <summary>
/// In-process host for the voice WS server, with the live Gemini transport substituted by an
/// in-process <see cref="FakeRealtimeTransport"/>. Tests pump events into the fake via
/// <see cref="FakeFactory.Latest"/>.
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
    /// Fake transport factory. Captures every transport it hands out so tests can drive events.
    /// </summary>
    internal sealed class FakeFactory : IRealtimeTransportFactory
    {
        private readonly object _gate = new();
        private FakeRealtimeTransport? _latest;
        private TaskCompletionSource<FakeRealtimeTransport> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The most recently allocated fake transport. Null until <see cref="Create"/> has been called.</summary>
        public FakeRealtimeTransport? Latest
        {
            get { lock (_gate) { return _latest; } }
        }

        /// <summary>Waits up to <paramref name="timeout"/> for the next <see cref="Create"/> call and returns the resulting fake.</summary>
        /// <param name="timeout">Maximum wait.</param>
        /// <returns>The fake transport handed to the next session.</returns>
        public async Task<FakeRealtimeTransport> WaitForCreateAsync(TimeSpan timeout)
        {
            Task<FakeRealtimeTransport> task;
            lock (_gate)
            {
                if (_latest is not null)
                {
                    return _latest;
                }
                task = _next.Task;
            }
            return await task.WaitAsync(timeout).ConfigureAwait(false);
        }

        public IRealtimeTransport Create()
        {
            var fake = new FakeRealtimeTransport();
            TaskCompletionSource<FakeRealtimeTransport> previous;
            lock (_gate)
            {
                _latest = fake;
                previous = _next;
                _next = new TaskCompletionSource<FakeRealtimeTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            previous.TrySetResult(fake);
            return fake;
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
