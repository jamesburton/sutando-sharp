using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sutando.Dashboard;
using Sutando.Tests.Api;
using Sutando.Workspace;

namespace Sutando.Tests.Dashboard;

/// <summary>
/// Integration tests for the read-only Sutando dashboard. Verifies HTML rendering, the
/// snapshot endpoint, healthz, and the SignalR hub broadcast events. Serialized with the
/// API tests via <see cref="WorkspaceCollection"/>.
/// </summary>
[Collection(WorkspaceCollection.Name)]
public sealed class DashboardTests : IDisposable
{
    private readonly string _tempRoot;

    public DashboardTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-dash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    private WebApplicationFactory<Program> CreateFactory(Action<WorkspaceDirectory>? seed = null)
    {
        var root = _tempRoot;
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WorkspaceRoot", root);
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [DashboardCommand.WorkspaceRootConfigKey] = root,
                });
            });
        });

        if (seed is not null)
        {
            // Force factory to build so the DI workspace points at root, then run seeding
            // against the same WorkspaceDirectory the host will use.
            using var scope = factory.Services.CreateScope();
            var ws = scope.ServiceProvider.GetRequiredService<WorkspaceDirectory>();
            seed(ws);
        }

        return factory;
    }

    [Fact]
    public async Task GetRoot_ReturnsHtml_MentioningCurrentStatus()
    {
        using var factory = CreateFactory(ws =>
        {
            new CoreStatus(ws).SignalRunning("indexing notes");
        });
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(resp.Content.Headers.ContentType);
        Assert.Equal("text/html", resp.Content.Headers.ContentType!.MediaType);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("running", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("indexing notes", html, StringComparison.Ordinal);
        Assert.Contains("sutando", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRoot_NoSignal_RendersUnknownStatus()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("no signal yet", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Healthz_ReturnsOk()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/healthz", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Snapshot_IncludesPendingTasksAndOwnerActivity()
    {
        using var factory = CreateFactory(ws =>
        {
            new CoreStatus(ws).SignalIdle();
            new OwnerActivity(ws).Record("chat", "last thing the human said");

            var envelope = new Sutando.Bridge.TaskEnvelope
            {
                Id = "task-chat-1",
                Timestamp = DateTimeOffset.UtcNow,
                Body = "find me a coffee",
                Source = Sutando.Bridge.TaskSource.Chat,
                ChannelId = "local-chat",
                UserId = "chat-local",
                AccessTier = Sutando.Bridge.AccessTier.Owner,
                Priority = Sutando.Bridge.TaskPriority.Normal,
            };
            Sutando.Bridge.TaskFile.Write(ws.Tasks.FullName, envelope);
        });
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/snapshot", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, json.GetProperty("pending_task_count").GetInt32());
        var firstTask = json.GetProperty("recent_tasks")[0];
        Assert.Equal("task-chat-1", firstTask.GetProperty("id").GetString());
        Assert.Equal("find me a coffee", firstTask.GetProperty("body_preview").GetString());
        Assert.Equal("chat", json.GetProperty("owner_activity").GetProperty("channel").GetString());
    }

    [Fact]
    public async Task Hub_BroadcastsCoreStatusChanged_WhenSignalIsCalled()
    {
        using var factory = CreateFactory();

        await using var connection = BuildHubConnection(factory);
        var tcs = new TaskCompletionSource<CoreStatusPayload?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<CoreStatusPayload?>(WorkspaceBroadcaster.CoreStatusChanged, payload =>
        {
            tcs.TrySetResult(payload);
        });

        await connection.StartAsync();

        // Drive the broadcast through the hosted-service surface so the test isn't at the
        // mercy of FileSystemWatcher timing on Windows/macOS/Linux.
        var ws = WorkspaceDirectory.Resolve();
        new CoreStatus(ws).SignalRunning("hello signalr");
        var broadcaster = factory.Services.GetRequiredService<IBroadcastChannel>();
        await broadcaster.BroadcastCoreStatusAsync();

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(received);
        Assert.Equal("running", received!.Status);
        Assert.Equal("hello signalr", received.Step);
    }

    [Fact]
    public async Task Hub_BroadcastsTaskAdded_OnExplicitTrigger()
    {
        using var factory = CreateFactory();
        await using var connection = BuildHubConnection(factory);
        var tcs = new TaskCompletionSource<TaskAddedPayload?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<TaskAddedPayload?>(WorkspaceBroadcaster.TaskAdded, p => tcs.TrySetResult(p));

        await connection.StartAsync();

        var broadcaster = factory.Services.GetRequiredService<IBroadcastChannel>();
        await broadcaster.BroadcastTaskAddedAsync("/tmp/task-x-1.txt");

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(received);
        Assert.Equal("task-x-1", received!.Id);
    }

    private static HubConnection BuildHubConnection(WebApplicationFactory<Program> factory)
    {
        // Server.CreateHandler() does not support WebSockets, so we force long-polling and
        // route the HTTP client through the test server's handler.
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress, "/hub/status"),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                })
            .Build();
    }
}
