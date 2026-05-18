using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sutando.Api;
using Sutando.Bridge;
using Sutando.Workspace;

namespace Sutando.Tests.Api;

/// <summary>
/// Integration tests for the Sutando HTTP API. Each test spins up a fresh
/// <see cref="WebApplicationFactory{TEntryPoint}"/> with a per-test temp workspace injected
/// through configuration. Tests in this class run serially with the dashboard tests via
/// <see cref="WorkspaceCollection"/> because both mutate process-global env vars during
/// DI bring-up.
/// </summary>
[Collection(WorkspaceCollection.Name)]
public sealed class ApiEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _tempRoot;
    private readonly string? _previousToken;

    public ApiEndpointsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _previousToken = Environment.GetEnvironmentVariable(ApiCommand.TokenEnvVar);
        Environment.SetEnvironmentVariable(ApiCommand.TokenEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ApiCommand.TokenEnvVar, _previousToken);
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    private WebApplicationFactory<Program> CreateFactory(string? bearerToken = null)
    {
        // Snapshot the token BEFORE building the factory so the resolved ApiAuth picks it up.
        Environment.SetEnvironmentVariable(ApiCommand.TokenEnvVar, bearerToken);
        var root = _tempRoot;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WorkspaceRoot", root);
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ApiCommand.WorkspaceRootConfigKey] = root,
                });
            });
        });
    }

    [Fact]
    public async Task Healthz_ReturnsOk_WithoutAuth()
    {
        using var factory = CreateFactory(bearerToken: "topsecret");
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/healthz", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("uptime_seconds").GetInt64() >= 0);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("workspace").GetString()));
    }

    [Fact]
    public async Task PostTasks_WritesEnvelope_And_Returns202()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var req = new
        {
            body = "do the thing",
            priority = "normal",
            timeout_ms = 600000,
            channel_id = "api-client-1",
            user_id = "alice",
        };

        var resp = await client.PostAsJsonAsync("/tasks", req, Json);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = body.GetProperty("id").GetString();
        var path = body.GetProperty("path").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        Assert.StartsWith("task-api-", id, StringComparison.Ordinal);
        Assert.True(File.Exists(path));

        var envelope = TaskFile.ParseFile(path!);
        Assert.Equal("do the thing", envelope.Body);
        Assert.Equal(TaskSource.Api, envelope.Source);
        Assert.Equal("api-client-1", envelope.ChannelId);
        Assert.Equal("alice", envelope.UserId);
        Assert.Equal(AccessTier.Verified, envelope.AccessTier);
        Assert.Equal(TaskPriority.Normal, envelope.Priority);
        Assert.Equal(TimeSpan.FromMilliseconds(600000), envelope.Timeout);
    }

    [Fact]
    public async Task PostTasks_AppliesDefaultsWhenOptionalFieldsMissing()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/tasks", new { body = "just the body" }, Json);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        var path = body.GetProperty("path").GetString();

        var envelope = TaskFile.ParseFile(path!);
        Assert.Equal("api-default", envelope.ChannelId);
        Assert.Equal("api-client", envelope.UserId);
        Assert.Equal(TaskPriority.Normal, envelope.Priority);
        Assert.Equal(AccessTier.Verified, envelope.AccessTier);
        Assert.Null(envelope.Timeout);
    }

    [Fact]
    public async Task PostTasks_RejectsEmptyBody()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/tasks", new { body = "" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetTasks_ListsPendingTasksManifest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/tasks", new { body = "first" }, Json);
        await client.PostAsJsonAsync("/tasks", new { body = "second", priority = "low" }, Json);

        var resp = await client.GetAsync(new Uri("/tasks", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        Assert.Equal(2, list.GetArrayLength());

        // Manifest entries have all required snake_case fields.
        foreach (var entry in list.EnumerateArray())
        {
            Assert.True(entry.TryGetProperty("id", out _));
            Assert.True(entry.TryGetProperty("source", out _));
            Assert.True(entry.TryGetProperty("priority", out _));
            Assert.True(entry.TryGetProperty("timestamp", out _));
            Assert.True(entry.TryGetProperty("body_preview", out _));
        }
    }

    [Fact]
    public async Task GetTask_ReturnsTaskAndResultWhenCompleted()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/tasks", new { body = "find the cat" }, Json);
        var posted = await post.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = posted.GetProperty("id").GetString()!;

        // Simulate the executor writing a result.
        var workspaceRoot = _tempRoot;
        var resultPath = Path.Combine(workspaceRoot, "results", id + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        await File.WriteAllTextAsync(resultPath, "found the cat under the couch.");

        var resp = await client.GetAsync(new Uri($"/tasks/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var detail = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(id, detail.GetProperty("id").GetString());
        Assert.Equal("completed", detail.GetProperty("status").GetString());
        Assert.Equal("found the cat under the couch.", detail.GetProperty("result").GetString());
        Assert.Equal("find the cat", detail.GetProperty("task").GetProperty("body_preview").GetString());
    }

    [Fact]
    public async Task GetTask_ReturnsPendingWhenNoResult()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/tasks", new { body = "waiting" }, Json);
        var posted = await post.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = posted.GetProperty("id").GetString()!;

        var resp = await client.GetAsync(new Uri($"/tasks/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var detail = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("pending", detail.GetProperty("status").GetString());
        // result is omitted when null because the serializer ignores null members.
        Assert.False(detail.TryGetProperty("result", out var resultProp) &&
                     resultProp.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task GetTask_404WhenAbsent()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/tasks/task-does-not-exist", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetTask_RejectsTraversal()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Slashes in the route segment get URL-encoded by HttpClient; the literal one we test
        // for is the sanitizer's job to reject (it disallows /, \, ..).
        var resp = await client.GetAsync(new Uri("/tasks/..%2Fbad", UriKind.Relative));
        Assert.True(resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetStatus_ReturnsCoreStatusPayload()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Seed core-status.json against the SAME workspace the factory's DI built. Resolving
        // a fresh WorkspaceDirectory here would pick the default ~/.sutando/workspace and
        // miss the per-test temp dir.
        using (var scope = factory.Services.CreateScope())
        {
            var ws = scope.ServiceProvider.GetRequiredService<WorkspaceDirectory>();
            new CoreStatus(ws).SignalRunning("running tests");
        }

        var resp = await client.GetAsync(new Uri("/status", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("running", body.GetProperty("status").GetString());
        Assert.Equal("running tests", body.GetProperty("step").GetString());
    }

    [Fact]
    public async Task BearerAuth_RejectsWhenTokenSet_And_AllowsWithToken()
    {
        const string token = "supersecret-token";
        using var factory = CreateFactory(bearerToken: token);
        using var client = factory.CreateClient();

        // No header → 401.
        var unauth = await client.PostAsJsonAsync("/tasks", new { body = "x" }, Json);
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        // Wrong token → 401.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        var bad = await client.PostAsJsonAsync("/tasks", new { body = "x" }, Json);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        // Right token → 202.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var ok = await client.PostAsJsonAsync("/tasks", new { body = "x" }, Json);
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);

        // Healthz remains open even with auth enabled.
        client.DefaultRequestHeaders.Authorization = null;
        var hz = await client.GetAsync(new Uri("/healthz", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, hz.StatusCode);
    }

    [Fact]
    public async Task GetTask_FindsArchivedTaskAndResult()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Plant an archived task + result under the month-partitioned layout.
        var id = "task-api-archived";
        var ym = DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var tasksArchive = Path.Combine(_tempRoot, "tasks", "archive", ym);
        var resultsArchive = Path.Combine(_tempRoot, "results", "archive", ym);
        Directory.CreateDirectory(tasksArchive);
        Directory.CreateDirectory(resultsArchive);

        var envelope = new TaskEnvelope
        {
            Id = id,
            Timestamp = DateTimeOffset.UtcNow,
            Body = "old task",
            Source = TaskSource.Api,
            ChannelId = "api-default",
            UserId = "api-client",
            AccessTier = AccessTier.Verified,
            Priority = TaskPriority.Normal,
        };
        await File.WriteAllTextAsync(Path.Combine(tasksArchive, id + ".txt"), TaskFile.Serialize(envelope));
        await File.WriteAllTextAsync(Path.Combine(resultsArchive, id + ".txt"), "done long ago");

        var resp = await client.GetAsync(new Uri($"/tasks/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var detail = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("completed", detail.GetProperty("status").GetString());
        Assert.Equal("done long ago", detail.GetProperty("result").GetString());
    }
}
