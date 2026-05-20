using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Sutando.Phone;
using Sutando.Realtime;
using Sutando.Tests.Realtime;

namespace Sutando.Tests.Phone;

/// <summary>
/// In-process host for the phone bridge, with the live Gemini transport substituted by an
/// in-process <see cref="FakeRealtimeClient"/> and the live Twilio REST client substituted
/// by a recording fake.
/// </summary>
/// <remarks>
/// Each test gets its own workspace dir under <c>%TEMP%\sutando-phone-tests\&lt;guid&gt;</c>
/// so the call-metadata writes don't bleed across cases. The host disables Twilio signature
/// validation in dev mode (<c>SUTANDO_PHONE_ALLOW_UNSIGNED=true</c>) — individual signature
/// tests explicitly re-enable it.
/// </remarks>
internal sealed class PhoneTestHost : WebApplicationFactory<global::Sutando.Phone.Program>
{
    public FakeTransportFactory TransportFactory { get; } = new();
    public RecordingTwilioRestClient RestClient { get; } = new();
    public string WorkspaceRoot { get; }

    /// <summary>Build a host with the given options applied on top of the defaults.</summary>
    /// <param name="configure">Hook to mutate config / DI before the host starts.</param>
    /// <param name="signatureBypass">
    ///   When <see langword="true"/> (the default), the host enables the unsigned-webhook bypass
    ///   so tests can drive the webhook endpoints without computing an HMAC-SHA1 signature.
    ///   The two signature-validation tests pass <see langword="false"/> to disable the bypass.
    /// </param>
    public PhoneTestHost(
        Action<IConfigurationBuilder, IServiceCollection>? configure = null,
        bool signatureBypass = true)
    {
        WorkspaceRoot = Path.Combine(Path.GetTempPath(), "sutando-phone-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkspaceRoot);

        Environment.SetEnvironmentVariable(PhoneEnv.GeminiVoiceKey, "test-key");
        // Default: allow unsigned webhooks. The signature-validation tests instead pass
        // signatureBypass=false to exercise the real RequestValidator code path.
        Environment.SetEnvironmentVariable(PhoneEnv.AllowUnsigned, signatureBypass ? "true" : "false");

        _configure = configure;
    }

    private readonly Action<IConfigurationBuilder, IServiceCollection>? _configure;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkspaceRoot"] = WorkspaceRoot,
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPhoneTransportFactory>();
            services.AddSingleton<IPhoneTransportFactory>(TransportFactory);
            services.RemoveAll<ITwilioRestClient>();
            services.AddSingleton<ITwilioRestClient>(RestClient);
        });

        if (_configure is not null)
        {
            // Apply user customisation last so it wins over our defaults.
            builder.ConfigureAppConfiguration(cfg => _configure(cfg, new ServiceCollection()));
        }
    }

    /// <summary>Recording stand-in for the Twilio REST client — captures every outbound call request.</summary>
    internal sealed class RecordingTwilioRestClient : ITwilioRestClient
    {
        public List<(string To, string From, string? Twiml)> Calls { get; } = new();

        /// <summary>Set this to true to make <see cref="CreateCallAsync"/> throw — exercises the 503 path.</summary>
        public bool ThrowNotSupported { get; set; }

        public Task<string> CreateCallAsync(string to, string from, Uri? twimlUrl, string? twiml, Uri? statusCallback, CancellationToken ct)
        {
            if (ThrowNotSupported)
            {
                throw new NotSupportedException("simulated missing creds");
            }
            Calls.Add((to, from, twiml));
            return Task.FromResult("CA" + Guid.NewGuid().ToString("N"));
        }
    }

    /// <summary>Fake realtime-client factory — same pattern as VoiceTestHost.</summary>
    internal sealed class FakeTransportFactory : IPhoneTransportFactory
    {
        private readonly object _gate = new();
        private FakeRealtimeClient? _latest;
        private TaskCompletionSource<FakeRealtimeClient> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeRealtimeClient? LatestClient
        {
            get { lock (_gate) { return _latest; } }
        }

        public FakeRealtimeClientSession? LatestSession => LatestClient?.LatestSession;

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

/// <summary>Helper extension to mirror the upstream Voice test host's <c>RemoveAll</c>.</summary>
internal static class PhoneServiceCollectionExtensions
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
