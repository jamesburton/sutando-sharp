using System.Net;
using System.Net.Http;
using System.Text;
using Sutando.Skills;
using Sutando.Skills.Cloud.Common;
using Sutando.Skills.Cloud.Twitter;
using Sutando.Workspace;

namespace Sutando.Tests.Skills.Cloud;

/// <summary>
/// Fake-HTTP coverage for <see cref="XTwitterSkill"/>. Verifies the v2 tweet-create payload,
/// OAuth1 Authorization header, and the success/error paths around the response shape.
/// </summary>
public sealed class XTwitterSkillTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    private static readonly Dictionary<string, string> ValidCreds = new()
    {
        [XTwitterSkill.ApiKeyEnvVar] = "consumer-key",
        [XTwitterSkill.ApiSecretEnvVar] = "consumer-secret",
        [XTwitterSkill.AccessTokenEnvVar] = "access-token",
        [XTwitterSkill.AccessSecretEnvVar] = "access-secret",
    };

    public XTwitterSkillTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-x-twitter-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task ExecuteAsync_HappyPath_PostsToV2EndpointWithOAuth1AndReturnsTweetId()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                "{\"data\":{\"id\":\"1234567890\",\"text\":\"hello world\"}}",
                Encoding.UTF8, "application/json"),
        });

        // Deterministic signer so the Authorization header is reproducible.
        var skill = new XTwitterSkill(XTwitterSkill.DefaultManifest(),
            new OAuth1Signer(() => "test-nonce", () => 1700000000L));

        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hello world" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("1234567890", result.Body);
        Assert.Contains("x.com/i/web/status/1234567890", result.Body);
        Assert.Empty(result.Artifacts);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.twitter.com/2/tweets", request.RequestUri?.AbsoluteUri);

        // OAuth1 header populated with all six oauth_* params + signature.
        Assert.True(request.RequestHeaders.TryGetValue("Authorization", out var authValues));
        var auth = string.Join(' ', authValues);
        Assert.StartsWith("OAuth ", auth);
        Assert.Contains("oauth_consumer_key=\"consumer-key\"", auth);
        Assert.Contains("oauth_nonce=\"test-nonce\"", auth);
        Assert.Contains("oauth_timestamp=\"1700000000\"", auth);
        Assert.Contains("oauth_token=\"access-token\"", auth);
        Assert.Contains("oauth_signature_method=\"HMAC-SHA1\"", auth);
        Assert.Contains("oauth_signature=", auth);

        // JSON body carries the tweet text.
        var sentBody = request.BodyAsString();
        Assert.Contains("\"text\":\"hello world\"", sentBody);
    }

    [Theory]
    [InlineData(XTwitterSkill.ApiKeyEnvVar)]
    [InlineData(XTwitterSkill.ApiSecretEnvVar)]
    [InlineData(XTwitterSkill.AccessTokenEnvVar)]
    [InlineData(XTwitterSkill.AccessSecretEnvVar)]
    public async Task ExecuteAsync_AnyMissingCredential_FailsCleanlyWithoutHttp(string envVarToOmit)
    {
        var creds = new Dictionary<string, string>(ValidCreds);
        creds.Remove(envVarToOmit);

        var handler = new FakeHttpMessageHandler();
        var skill = new XTwitterSkill();
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: creds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hi" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(envVarToOmit, result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_MissingText_FailsCleanlyWithoutHttp()
    {
        var handler = new FakeHttpMessageHandler();
        var skill = new XTwitterSkill();
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'text'", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_Non2xx_FailsWithStatusInError()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                "{\"title\":\"Unauthorized\",\"status\":401}",
                Encoding.UTF8, "application/problem+json"),
        });

        var skill = new XTwitterSkill();
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hi" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("401", result.Error);
        Assert.Contains("Unauthorized", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ResponseWithoutTweetId_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"data\":{}}", Encoding.UTF8, "application/json"),
        });

        var skill = new XTwitterSkill();
        var ctx = new SkillContext(_workspace, skillRoot: _tempRoot, http: new HttpClient(handler), env: ValidCreds);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hi" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("tweet id", result.Error);
    }

    [Fact]
    public void DefaultManifest_HasExpectedShape()
    {
        var m = XTwitterSkill.DefaultManifest();
        Assert.Equal("x-twitter", m.Id);
        Assert.Equal(SkillRuntime.Managed, m.Runtime);
        Assert.Equal("Sutando.Skills.Cloud.Twitter.XTwitterSkill, Sutando.Skills.Cloud", m.Entry);
        Assert.Contains("http-out", m.Capabilities);
        Assert.Contains("tweet", m.Triggers);
    }

    [Fact]
    public void RequiredEnvVars_ListsAllFourOAuthFields()
    {
        Assert.Equal(4, XTwitterSkill.RequiredEnvVars.Count);
        Assert.Contains(XTwitterSkill.ApiKeyEnvVar, XTwitterSkill.RequiredEnvVars);
        Assert.Contains(XTwitterSkill.ApiSecretEnvVar, XTwitterSkill.RequiredEnvVars);
        Assert.Contains(XTwitterSkill.AccessTokenEnvVar, XTwitterSkill.RequiredEnvVars);
        Assert.Contains(XTwitterSkill.AccessSecretEnvVar, XTwitterSkill.RequiredEnvVars);
    }
}
