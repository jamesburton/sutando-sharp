using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Sutando.Skills;
using Sutando.Skills.Cloud.Google;
using Sutando.Workspace;

namespace Sutando.Tests.Skills.Cloud;

/// <summary>
/// Fake-HTTP coverage for <see cref="GeminiImageGenerationSkill"/>. Same shape as the TTS
/// tests: stub the endpoint, drive the skill, assert that returned image bytes land on disk
/// with the right extension and that any text rationale flows into <see cref="SkillResult.Body"/>.
/// </summary>
public sealed class GeminiImageGenerationSkillTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public GeminiImageGenerationSkillTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-image-gen-" + Guid.NewGuid().ToString("N"));
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
    public async Task ExecuteAsync_HappyPath_WritesPngArtifactWithCorrectExtension()
    {
        var imgBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD };
        var responseBody = ResponseFor("image/png", Convert.ToBase64String(imgBytes), text: null);

        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });

        var skill = new GeminiImageGenerationSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [GeminiImageGenerationSkill.ApiKeyEnvVar] = "fake-key" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["prompt"] = "a red square" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var path = Assert.Single(result.Artifacts);
        Assert.EndsWith(".png", path, StringComparison.OrdinalIgnoreCase);
        var written = await File.ReadAllBytesAsync(path);
        Assert.Equal(imgBytes, written);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("gemini-2.0-flash-preview-image-generation:generateContent", request.RequestUri!.AbsoluteUri);
        Assert.Contains("key=fake-key", request.RequestUri.Query);
        Assert.Contains("a red square", request.BodyAsString());
        Assert.Contains("\"responseModalities\"", request.BodyAsString());
    }

    [Fact]
    public async Task ExecuteAsync_JpegMimeType_LandsAsJpgExtension()
    {
        // The wire format uses "image/jpeg" but the conventional file extension is .jpg.
        // Verify the mime → extension mapping handles that asymmetry.
        var imgBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var responseBody = ResponseFor("image/jpeg", Convert.ToBase64String(imgBytes), text: null);

        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });

        var skill = new GeminiImageGenerationSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [GeminiImageGenerationSkill.ApiKeyEnvVar] = "fake-key" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["prompt"] = "a sunset" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.EndsWith(".jpg", result.Artifacts[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_TextRationaleFlowsIntoBody()
    {
        // The image-modality endpoint commonly returns a text part alongside the image — model
        // rationale, caption, etc. Ensure it lands in result.Body so the caller can see it.
        var responseBody = ResponseFor("image/png", Convert.ToBase64String(new byte[] { 0x01 }), text: "I drew a red square.");

        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });

        var skill = new GeminiImageGenerationSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [GeminiImageGenerationSkill.ApiKeyEnvVar] = "fake-key" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["prompt"] = "draw something" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("I drew a red square.", result.Body);
        Assert.Single(result.Artifacts);
    }

    [Fact]
    public async Task ExecuteAsync_MissingApiKey_FailsCleanlyWithoutHttp()
    {
        var handler = new FakeHttpMessageHandler();
        var skill = new GeminiImageGenerationSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string>());

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["prompt"] = "hi" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(GeminiImageGenerationSkill.ApiKeyEnvVar, result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_MissingPrompt_FailsCleanlyWithoutHttp()
    {
        var handler = new FakeHttpMessageHandler();
        var skill = new GeminiImageGenerationSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [GeminiImageGenerationSkill.ApiKeyEnvVar] = "fake-key" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'prompt'", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_Non200_FailsWithStatusInError()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":{\"message\":\"quota exceeded\"}}", Encoding.UTF8, "application/json"),
        });

        var skill = new GeminiImageGenerationSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [GeminiImageGenerationSkill.ApiKeyEnvVar] = "fake-key" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["prompt"] = "hi" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("429", result.Error);
        Assert.Contains("quota exceeded", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ResponseWithNoImage_FailsCleanly()
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new { content = new { parts = new[] { new { text = "I can't generate images right now." } } } },
            },
        });

        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });

        var skill = new GeminiImageGenerationSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [GeminiImageGenerationSkill.ApiKeyEnvVar] = "fake-key" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["prompt"] = "hi" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no inline image", result.Error);
    }

    [Fact]
    public void DefaultManifest_HasExpectedShape()
    {
        var m = GeminiImageGenerationSkill.DefaultManifest();
        Assert.Equal("image-generation", m.Id);
        Assert.Equal(SkillRuntime.Managed, m.Runtime);
        Assert.Equal("Sutando.Skills.Cloud.Google.GeminiImageGenerationSkill, Sutando.Skills.Cloud", m.Entry);
        Assert.Contains("http-out", m.Capabilities);
        Assert.Contains("image", m.Capabilities);
        Assert.Contains("image-generation", m.Triggers);
    }

    private static string ResponseFor(string mimeType, string dataB64, string? text)
    {
        var parts = new List<object>
        {
            new { inlineData = new { mimeType, data = dataB64 } },
        };
        if (text is not null)
        {
            parts.Add(new { text });
        }
        return JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts } } },
        });
    }
}
