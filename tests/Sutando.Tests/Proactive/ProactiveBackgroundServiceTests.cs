using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Sutando.Proactive;
using Sutando.Workspace;

namespace Sutando.Tests.Proactive;

/// <summary>
/// Wiring tests for <see cref="ProactiveBackgroundService"/>: in-memory cron config, fake
/// clock, capture pass to count invocations. We construct the service directly rather than
/// via a full <see cref="Host"/> so the test never races the host's hosted-service launcher
/// — `host.StartAsync` may run BackgroundServices on a thread-pool thread, which makes
/// "advance fake time immediately after StartAsync" flaky. Direct construction + manual
/// StartAsync removes that race entirely.
/// </summary>
public sealed class ProactiveBackgroundServiceTests : IDisposable
{
    private static readonly DateTimeOffset StartUtc = new(2026, 5, 25, 0, 0, 0, TimeSpan.Zero);

    private readonly string _workspaceRoot;

    public ProactiveBackgroundServiceTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "sutando-proactive-svc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspaceRoot))
            {
                Directory.Delete(_workspaceRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public async Task ExecuteAsync_FiresConfiguredPassOnSchedule()
    {
        File.WriteAllText(
            Path.Combine(_workspaceRoot, CronConfigLoader.PrimaryFileName),
            """[ { "name": "every-min", "cron": "* * * * *", "prompt": "p" } ]""");

        await using var harness = BuildHarness();
        await harness.Service.StartAsync(CancellationToken.None).ConfigureAwait(true);

        // Advance 3 minutes — pass should run exactly 3 times.
        harness.Fake.Advance(TimeSpan.FromMinutes(3));

        // Per-pass dispatch is fire-and-forget on the TimeProvider callback; give the
        // continuation a window to run before we read counts.
        await WaitForAsync(() => harness.Pass.InvocationCount >= 3, TimeSpan.FromSeconds(2)).ConfigureAwait(true);

        Assert.Equal(3, harness.Pass.InvocationCount);
        Assert.All(harness.Pass.Captured, ctx =>
        {
            Assert.NotNull(ctx.TriggeringEntry);
            Assert.Equal("every-min", ctx.TriggeringEntry!.Name);
            Assert.NotNull(ctx.Workspace);
        });

        await harness.Service.StopAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [Fact]
    public async Task ExecuteAsync_NoCronConfig_RunsButStaysInert()
    {
        await using var harness = BuildHarness();
        await harness.Service.StartAsync(CancellationToken.None).ConfigureAwait(true);

        harness.Fake.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50).ConfigureAwait(true);

        Assert.Equal(0, harness.Pass.InvocationCount);
        Assert.Equal(0, harness.Service.ScheduledEntryCount);

        await harness.Service.StopAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [Fact]
    public async Task ExecuteAsync_StopHaltsFurtherDispatches()
    {
        File.WriteAllText(
            Path.Combine(_workspaceRoot, CronConfigLoader.PrimaryFileName),
            """[ { "name": "every-min", "cron": "* * * * *", "prompt": "p" } ]""");

        await using var harness = BuildHarness();
        await harness.Service.StartAsync(CancellationToken.None).ConfigureAwait(true);

        harness.Fake.Advance(TimeSpan.FromMinutes(2));
        await WaitForAsync(() => harness.Pass.InvocationCount >= 2, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        Assert.Equal(2, harness.Pass.InvocationCount);

        await harness.Service.StopAsync(CancellationToken.None).ConfigureAwait(true);

        harness.Fake.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(50).ConfigureAwait(true);

        Assert.Equal(2, harness.Pass.InvocationCount);
    }

    [Fact]
    public async Task ExecuteAsync_DispatchedPassCount_TracksFires()
    {
        File.WriteAllText(
            Path.Combine(_workspaceRoot, CronConfigLoader.PrimaryFileName),
            """[ { "name": "every-min", "cron": "* * * * *", "prompt": "p" } ]""");

        await using var harness = BuildHarness();
        await harness.Service.StartAsync(CancellationToken.None).ConfigureAwait(true);

        harness.Fake.Advance(TimeSpan.FromMinutes(2));
        await WaitForAsync(() => harness.Service.DispatchedPassCount >= 2, TimeSpan.FromSeconds(2)).ConfigureAwait(true);

        Assert.Equal(2, harness.Service.DispatchedPassCount);
        Assert.Equal(1, harness.Service.ScheduledEntryCount);

        await harness.Service.StopAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [Fact]
    public void AddSutandoProactive_RegistersExpectedServices()
    {
        var services = new ServiceCollection();

        // Use the test's temp workspace, NOT the user's home — otherwise we'd pollute
        // ~/.sutando/workspace/ on every CI / local test run.
        services.AddSingleton(MakeWorkspace(_workspaceRoot));
        services.AddSutandoProactive();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ICronScheduler>());
        Assert.NotNull(provider.GetRequiredService<CronConfigLoader>());
        Assert.NotNull(provider.GetRequiredService<TimeProvider>());

        using var scope = provider.CreateScope();
        var pass = scope.ServiceProvider.GetRequiredService<IProactivePass>();
        Assert.IsType<NoopProactivePass>(pass);
    }

    private Harness BuildHarness()
    {
        var fake = new FakeTimeProvider(StartUtc);
        var pass = new CapturingPass();
        var workspace = MakeWorkspace(_workspaceRoot);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fake);
        services.AddSingleton(workspace);
        services.AddSingleton<IProactivePass>(pass);
        services.AddSutandoProactive();

        var provider = services.BuildServiceProvider();

        // Construct ProactiveBackgroundService directly. Going through the full Host pipeline
        // adds nondeterminism around when ExecuteAsync actually runs — irrelevant to what
        // we're verifying (the scheduler+pass plumbing), and a source of test flakes.
        var scheduler = provider.GetRequiredService<ICronScheduler>();
        var loader = provider.GetRequiredService<CronConfigLoader>();
        var service = new ProactiveBackgroundService(provider, scheduler, loader, workspace, fake);

        return new Harness(provider, service, fake, pass);
    }

    private static WorkspaceDirectory MakeWorkspace(string root)
    {
        var previous = Environment.GetEnvironmentVariable(WorkspaceDirectory.EnvVar);
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, root);
        try
        {
            return WorkspaceDirectory.Resolve(legacyFallback: null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, previous);
        }
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(ServiceProvider provider, ProactiveBackgroundService service, FakeTimeProvider fake, CapturingPass pass)
        {
            Provider = provider;
            Service = service;
            Fake = fake;
            Pass = pass;
        }

        public ServiceProvider Provider { get; }

        public ProactiveBackgroundService Service { get; }

        public FakeTimeProvider Fake { get; }

        public CapturingPass Pass { get; }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await Provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class CapturingPass : IProactivePass
    {
        private readonly List<ProactivePassContext> _captured = new();
        private int _count;

        public int InvocationCount => Volatile.Read(ref _count);

        public IReadOnlyList<ProactivePassContext> Captured
        {
            get
            {
                lock (_captured)
                {
                    return _captured.ToArray();
                }
            }
        }

        public Task RunAsync(ProactivePassContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            lock (_captured)
            {
                _captured.Add(context);
            }
            return Task.CompletedTask;
        }
    }
}
