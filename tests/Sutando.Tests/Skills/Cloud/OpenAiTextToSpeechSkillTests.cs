using System.Net;
using System.Net.Http;
using System.Text;
using Sutando.Skills;
using Sutando.Skills.Cloud.OpenAI;
using Sutando.Workspace;

namespace Sutando.Tests.Skills.Cloud;

/// <summary>
/// Fake-HTTP coverage for <see cref="OpenAiTextToSpeechSkill"/>. Mirrors the
/// <see cref="GeminiTextToSpeechSkillTests"/> shape: stub the endpoint, drive the skill,
/// assert the request was authenticated and the response bytes landed as an artifact.
/// </summary>
public sealed class OpenAiTextToSpeechSkillTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public OpenAiTextToSpeechSkillTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-openai-tts-" + Guid.NewGuid().ToString("N"));
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
    public async Task ExecuteAsync_HappyPath_WritesMp3ArtifactAndSendsBearerAuth()
    {
        // Bytes don't have to be a valid mp3 — the skill writes the raw response body verbatim.
        // Using a recognisable byte pattern lets us assert "exactly these bytes hit disk".
        var responseBytes = new byte[] { 0xFF, 0xFB, 0x90, 0x44, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };

        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseBytes)
            {
                Headers = { { "Content-Type", "audio/mpeg" } },
            },
        });

        var skill = new OpenAiTextToSpeechSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [OpenAiTextToSpeechSkill.ApiKeyEnvVar] = "sk-fake" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hello world" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var artifactPath = Assert.Single(result.Artifacts);
        Assert.True(File.Exists(artifactPath), $"expected artifact at {artifactPath}");
        Assert.EndsWith(".mp3", artifactPath, StringComparison.OrdinalIgnoreCase);

        var written = await File.ReadAllBytesAsync(artifactPath);
        Assert.Equal(responseBytes, written);

        // Single request, with bearer auth and the configured endpoint + json body.
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.openai.com/v1/audio/speech", request.RequestUri?.AbsoluteUri);
        Assert.NotNull(request.Authorization);
        Assert.Equal("Bearer", request.Authorization!.Scheme);
        Assert.Equal("sk-fake", request.Authorization.Parameter);

        var sentBody = request.BodyAsString();
        Assert.Contains("\"input\":\"hello world\"", sentBody);
        Assert.Contains("\"voice\":\"alloy\"", sentBody);
        Assert.Contains("\"model\":\"tts-1\"", sentBody);
        Assert.Contains("\"response_format\":\"mp3\"", sentBody);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsFormatArgument_ChoosesMatchingExtension()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x01, 0x02 }),
        });

        var skill = new OpenAiTextToSpeechSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [OpenAiTextToSpeechSkill.ApiKeyEnvVar] = "sk-fake" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hi", ["format"] = "flac" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var path = Assert.Single(result.Artifacts);
        Assert.EndsWith(".flac", path, StringComparison.OrdinalIgnoreCase);

        var sentBody = handler.Requests[0].BodyAsString();
        Assert.Contains("\"response_format\":\"flac\"", sentBody);
    }

    [Fact]
    public async Task ExecuteAsync_MissingApiKey_FailsCleanlyWithoutHttp()
    {
        var handler = new FakeHttpMessageHandler();
        var skill = new OpenAiTextToSpeechSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string>());

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hi" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(OpenAiTextToSpeechSkill.ApiKeyEnvVar, result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_MissingText_FailsCleanlyWithoutHttp()
    {
        var handler = new FakeHttpMessageHandler();
        var skill = new OpenAiTextToSpeechSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [OpenAiTextToSpeechSkill.ApiKeyEnvVar] = "sk-fake" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'text'", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_Non200_FailsWithStatusInError()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":{\"message\":\"Incorrect API key\"}}", Encoding.UTF8, "application/json"),
        });

        var skill = new OpenAiTextToSpeechSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [OpenAiTextToSpeechSkill.ApiKeyEnvVar] = "bad" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hi" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("401", result.Error);
        Assert.Contains("Incorrect API key", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyBody_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]),
        });

        var skill = new OpenAiTextToSpeechSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [OpenAiTextToSpeechSkill.ApiKeyEnvVar] = "sk-fake" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hi" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("empty", result.Error);
    }

    [Fact]
    public void DefaultManifest_HasExpectedShape()
    {
        var m = OpenAiTextToSpeechSkill.DefaultManifest();
        Assert.Equal("openai-tts", m.Id);
        Assert.Equal(SkillRuntime.Managed, m.Runtime);
        Assert.Equal("Sutando.Skills.Cloud.OpenAI.OpenAiTextToSpeechSkill, Sutando.Skills.Cloud", m.Entry);
        Assert.Contains("http-out", m.Capabilities);
        Assert.Contains("audio", m.Capabilities);
        Assert.Contains("openai-tts", m.Triggers);
    }
}
