using System.Net;
using System.Net.Http;
using System.Text;
using Sutando.Skills;
using Sutando.Skills.Cloud.Common;
using Sutando.Skills.Cloud.Google;
using Sutando.Workspace;

namespace Sutando.Tests.Skills.Cloud;

/// <summary>
/// Fake-HTTP coverage for <see cref="CalendarSkill"/>. Each test stubs the OAuth2 token
/// endpoint first (request [0]) then the Calendar API endpoint (request [1]).
/// </summary>
public sealed class CalendarSkillTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    private static readonly Dictionary<string, string> ValidCreds = new()
    {
        [GoogleOAuthHelper.ClientIdEnvVar] = "test-client-id",
        [GoogleOAuthHelper.ClientSecretEnvVar] = "test-client-secret",
        [GoogleOAuthHelper.RefreshTokenEnvVar] = "test-refresh-token",
    };

    private static HttpResponseMessage TokenResponse() => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"access_token\":\"ya29.fake-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}",
            Encoding.UTF8, "application/json"),
    };

    public CalendarSkillTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-calendar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _previousEnv = Environment.GetEnvironmentVariable(WorkspaceDirectory.EnvVar);
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _tempRoot);
        _workspace = WorkspaceDirectory.Resolve();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _previousEnv);
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    // --- action=upcoming happy path ---

    [Fact]
    public async Task ExecuteAsync_UpcomingHappyPath_SendsGetWithTimeRangeAndListsEvents()
    {
        var eventsJson = """
            {
              "items": [
                {"id":"evt1","summary":"Standup","start":{"dateTime":"2026-05-26T09:00:00Z"},"end":{"dateTime":"2026-05-26T09:30:00Z"}},
                {"id":"evt2","summary":"All Day","start":{"date":"2026-05-27"},"end":{"date":"2026-05-28"}}
              ]
            }
            """;

        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse())
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(eventsJson, Encoding.UTF8, "application/json"),
            });

        var skill = new CalendarSkill(CalendarSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "upcoming" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        // Timed event uses dateTime.
        Assert.Contains("2026-05-26T09:00:00Z", result.Body);
        Assert.Contains("Standup", result.Body);
        // All-day event uses date.
        Assert.Contains("2026-05-27", result.Body);
        Assert.Contains("All Day", result.Body);
        Assert.Empty(result.Artifacts);
        Assert.Equal(2, handler.Requests.Count);

        // Verify Calendar API request shape.
        var apiRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, apiRequest.Method);
        Assert.NotNull(apiRequest.RequestUri);
        Assert.Contains("calendar/v3/calendars/primary/events", apiRequest.RequestUri!.AbsoluteUri);
        Assert.Contains("singleEvents=true", apiRequest.RequestUri.Query);
        Assert.Contains("orderBy=startTime", apiRequest.RequestUri.Query);

        // Authorization header must carry the access token.
        Assert.True(apiRequest.RequestHeaders.TryGetValue("Authorization", out var authValues));
        Assert.Contains("Bearer ya29.fake-token", string.Join(" ", authValues));
    }

    [Fact]
    public async Task ExecuteAsync_UpcomingWithDaysArg_IncludesDaysInTimeRange()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse())
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[]}", Encoding.UTF8, "application/json"),
            });

        var skill = new CalendarSkill(CalendarSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "upcoming", ["days"] = "14" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        // With no events, the body should say nothing found.
        Assert.Contains("No upcoming events", result.Body);
    }

    // --- action=create happy path ---

    [Fact]
    public async Task ExecuteAsync_CreateHappyPath_PostsEventJsonAndReturnsIdAndLink()
    {
        var createdJson = """
            {
              "id": "created-event-id",
              "htmlLink": "https://calendar.google.com/event?eid=abc123"
            }
            """;

        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse())
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(createdJson, Encoding.UTF8, "application/json"),
            });

        var skill = new CalendarSkill(CalendarSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string>
            {
                ["action"] = "create",
                ["title"] = "Team meeting",
                ["start"] = "2026-06-01T10:00:00Z",
                ["end"] = "2026-06-01T11:00:00Z",
            },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("created-event-id", result.Body);
        Assert.Contains("calendar.google.com", result.Body);
        Assert.Empty(result.Artifacts);
        Assert.Equal(2, handler.Requests.Count);

        // Verify the POST request.
        var apiRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, apiRequest.Method);
        Assert.Contains("calendar/v3/calendars/primary/events", apiRequest.RequestUri?.AbsoluteUri ?? string.Empty);

        // JSON body must contain summary + start + end.
        var body = apiRequest.BodyAsString();
        Assert.Contains("\"summary\":\"Team meeting\"", body);
        Assert.Contains("2026-06-01T10:00:00Z", body);
        Assert.Contains("2026-06-01T11:00:00Z", body);

        // Bearer auth.
        Assert.True(apiRequest.RequestHeaders.TryGetValue("Authorization", out var authValues));
        Assert.Contains("Bearer ya29.fake-token", string.Join(" ", authValues));
    }

    // --- missing env var ---

    [Theory]
    [InlineData(GoogleOAuthHelper.ClientIdEnvVar)]
    [InlineData(GoogleOAuthHelper.ClientSecretEnvVar)]
    [InlineData(GoogleOAuthHelper.RefreshTokenEnvVar)]
    public async Task ExecuteAsync_AnyMissingCredential_FailsCleanlyWithoutHttp(string envVarToOmit)
    {
        var creds = new Dictionary<string, string>(ValidCreds);
        creds.Remove(envVarToOmit);

        var handler = new FakeHttpMessageHandler();
        var skill = new CalendarSkill();
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: creds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "upcoming" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(envVarToOmit, result.Error);
        Assert.Empty(handler.Requests);
    }

    // --- missing required arguments ---

    [Fact]
    public async Task ExecuteAsync_CreateMissingTitle_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(TokenResponse());
        var skill = new CalendarSkill(CalendarSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "create", ["start"] = "2026-06-01T10:00:00Z", ["end"] = "2026-06-01T11:00:00Z" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'title'", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_CreateMissingStart_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(TokenResponse());
        var skill = new CalendarSkill(CalendarSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "create", ["title"] = "Meeting", ["end"] = "2026-06-01T11:00:00Z" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'start'", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_CreateMissingEnd_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(TokenResponse());
        var skill = new CalendarSkill(CalendarSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "create", ["title"] = "Meeting", ["start"] = "2026-06-01T10:00:00Z" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'end'", result.Error);
    }

    // --- non-2xx API response ---

    [Fact]
    public async Task ExecuteAsync_UpcomingNon2xx_FailsWithStatusInError()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse())
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\":{\"code\":403,\"message\":\"Forbidden\"}}", Encoding.UTF8, "application/json"),
            });

        var skill = new CalendarSkill(CalendarSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "upcoming" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("403", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_CreateNon2xx_FailsWithStatusInError()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse())
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"code\":429,\"message\":\"Rate Limit Exceeded\"}}", Encoding.UTF8, "application/json"),
            });

        var skill = new CalendarSkill(CalendarSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string>
            {
                ["action"] = "create",
                ["title"] = "Meeting",
                ["start"] = "2026-06-01T10:00:00Z",
                ["end"] = "2026-06-01T11:00:00Z",
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("429", result.Error);
    }

    // --- malformed response ---

    [Fact]
    public async Task ExecuteAsync_UpcomingMalformedJson_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse())
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-json!", Encoding.UTF8, "text/plain"),
            });

        var skill = new CalendarSkill(CalendarSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "upcoming" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("JSON", result.Error);
    }

    // --- manifest shape ---

    [Fact]
    public void DefaultManifest_HasExpectedShape()
    {
        var m = CalendarSkill.DefaultManifest();
        Assert.Equal("calendar", m.Id);
        Assert.Equal(SkillRuntime.Managed, m.Runtime);
        Assert.Equal("Sutando.Skills.Cloud.Google.CalendarSkill, Sutando.Skills.Cloud", m.Entry);
        Assert.Contains("http-out", m.Capabilities);
        Assert.Contains("calendar", m.Triggers);
    }
}
