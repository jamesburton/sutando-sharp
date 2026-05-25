using System.Net;
using System.Net.Http;
using System.Text;
using Sutando.Skills;
using Sutando.Skills.Cloud.Common;
using Sutando.Skills.Cloud.Google;
using Sutando.Workspace;

namespace Sutando.Tests.Skills.Cloud;

/// <summary>
/// Fake-HTTP coverage for <see cref="GmailSkill"/>. Each test stubs the OAuth2 token endpoint
/// first (request [0]) then the Gmail API endpoint (request [1]). Only the second request is
/// asserted in detail — the token exchange request is verified in
/// <see cref="GoogleOAuthHelperTests"/>.
/// </summary>
public sealed class GmailSkillTests : IDisposable
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

    public GmailSkillTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-gmail-" + Guid.NewGuid().ToString("N"));
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

    // --- action=search happy path ---

    [Fact]
    public async Task ExecuteAsync_SearchHappyPath_SendsGetWithQueryAndReturnsMessageList()
    {
        var apiResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"messages\":[{\"id\":\"msg1\",\"threadId\":\"thread1\"},{\"id\":\"msg2\",\"threadId\":\"thread2\"}],\"resultSizeEstimate\":2}",
                Encoding.UTF8, "application/json"),
        };

        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse()) // request[0]: token exchange
            .EnqueueResponse(apiResponse);    // request[1]: Gmail messages list

        var skill = new GmailSkill(GmailSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "search", ["query"] = "from:example@gmail.com" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("msg1", result.Body);
        Assert.Contains("thread1", result.Body);
        Assert.Contains("msg2", result.Body);
        Assert.Empty(result.Artifacts);
        Assert.Equal(2, handler.Requests.Count);

        // Verify the Gmail API request (second request).
        var apiRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, apiRequest.Method);
        Assert.NotNull(apiRequest.RequestUri);
        Assert.Contains("gmail.googleapis.com/gmail/v1/users/me/messages", apiRequest.RequestUri!.AbsoluteUri);
        Assert.Contains("from%3Aexample%40gmail.com", apiRequest.RequestUri.Query);

        // Authorization header must use the fetched access token.
        Assert.True(apiRequest.RequestHeaders.TryGetValue("Authorization", out var authValues));
        Assert.Contains("Bearer ya29.fake-token", string.Join(" ", authValues));
    }

    [Fact]
    public async Task ExecuteAsync_SearchWithMaxArg_IncludesMaxResultsInQuery()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse())
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"messages\":[]}", Encoding.UTF8, "application/json"),
            });

        var skill = new GmailSkill(GmailSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "search", ["query"] = "is:unread", ["max"] = "5" },
            CancellationToken.None);

        var apiRequest = handler.Requests[1];
        Assert.Contains("maxResults=5", apiRequest.RequestUri?.Query ?? string.Empty);
    }

    // --- action=get happy path ---

    [Fact]
    public async Task ExecuteAsync_GetHappyPath_ExtractsFromSubjectAndPlainTextBody()
    {
        // Plain-text body is base64url-encoded "Hello from Gmail."
        var plainBody = Convert.ToBase64String(Encoding.UTF8.GetBytes("Hello from Gmail."))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var messageJson = $$"""
            {
              "id": "msg1",
              "threadId": "thread1",
              "snippet": "Hello from Gmail.",
              "payload": {
                "mimeType": "text/plain",
                "headers": [
                  {"name": "From", "value": "sender@example.com"},
                  {"name": "Subject", "value": "Test Subject"}
                ],
                "body": {"data": "{{plainBody}}", "size": 18}
              }
            }
            """;

        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse())
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(messageJson, Encoding.UTF8, "application/json"),
            });

        var skill = new GmailSkill(GmailSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "get", ["id"] = "msg1" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("From: sender@example.com", result.Body);
        Assert.Contains("Subject: Test Subject", result.Body);
        Assert.Contains("Hello from Gmail.", result.Body);
        Assert.Empty(result.Artifacts);

        var apiRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, apiRequest.Method);
        Assert.Contains("/messages/msg1", apiRequest.RequestUri?.AbsoluteUri ?? string.Empty);
        Assert.Contains("format=full", apiRequest.RequestUri?.Query ?? string.Empty);
    }

    [Fact]
    public async Task ExecuteAsync_GetMultipartMessage_ExtractsNestedPlainTextPart()
    {
        var plainBody = Convert.ToBase64String(Encoding.UTF8.GetBytes("Nested plain text."))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var messageJson = $$"""
            {
              "id": "msg2",
              "payload": {
                "mimeType": "multipart/alternative",
                "headers": [
                  {"name": "From", "value": "a@b.com"},
                  {"name": "Subject", "value": "Multipart"}
                ],
                "parts": [
                  {
                    "mimeType": "text/plain",
                    "body": {"data": "{{plainBody}}", "size": 18}
                  },
                  {
                    "mimeType": "text/html",
                    "body": {"data": "PHA+SGk8L3A+", "size": 8}
                  }
                ]
              }
            }
            """;

        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse())
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(messageJson, Encoding.UTF8, "application/json"),
            });

        var skill = new GmailSkill(GmailSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "get", ["id"] = "msg2" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("Nested plain text.", result.Body);
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
        var skill = new GmailSkill();
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: creds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "search", ["query"] = "test" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(envVarToOmit, result.Error);
        Assert.Empty(handler.Requests);
    }

    // --- missing required argument ---

    [Fact]
    public async Task ExecuteAsync_SearchMissingQuery_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse());
        var skill = new GmailSkill(GmailSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "search" }, // no query
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'query'", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_GetMissingId_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse());
        var skill = new GmailSkill(GmailSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "get" }, // no id
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'id'", result.Error);
    }

    // --- non-2xx API response ---

    [Fact]
    public async Task ExecuteAsync_SearchNon2xx_FailsWithStatusInError()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse())
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":{\"code\":401,\"message\":\"Invalid Credentials\"}}", Encoding.UTF8, "application/json"),
            });

        var skill = new GmailSkill(GmailSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "search", ["query"] = "test" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("401", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_GetNon2xx_FailsWithStatusInError()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse())
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":{\"code\":404,\"message\":\"Not Found\"}}", Encoding.UTF8, "application/json"),
            });

        var skill = new GmailSkill(GmailSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "get", ["id"] = "nonexistent" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("404", result.Error);
    }

    // --- malformed response ---

    [Fact]
    public async Task ExecuteAsync_SearchMalformedJson_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse())
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-json", Encoding.UTF8, "text/plain"),
            });

        var skill = new GmailSkill(GmailSkill.DefaultManifest(), new GoogleOAuthHelper());
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["action"] = "search", ["query"] = "test" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("JSON", result.Error);
    }

    // --- manifest shape ---

    [Fact]
    public void DefaultManifest_HasExpectedShape()
    {
        var m = GmailSkill.DefaultManifest();
        Assert.Equal("gmail", m.Id);
        Assert.Equal(SkillRuntime.Managed, m.Runtime);
        Assert.Equal("Sutando.Skills.Cloud.Google.GmailSkill, Sutando.Skills.Cloud", m.Entry);
        Assert.Contains("http-out", m.Capabilities);
        Assert.Contains("gmail", m.Triggers);
    }
}
