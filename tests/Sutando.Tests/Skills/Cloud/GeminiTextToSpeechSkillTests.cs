using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Sutando.Skills;
using Sutando.Skills.Cloud.Google;
using Sutando.Workspace;

namespace Sutando.Tests.Skills.Cloud;

/// <summary>
/// Fake-HTTP coverage for <see cref="GeminiTextToSpeechSkill"/>. Drives the skill through
/// <see cref="ISkill.ExecuteAsync"/> with a <see cref="FakeHttpMessageHandler"/> standing in for
/// Google's generateContent endpoint, asserts the request body / URL / headers, and verifies
/// the response audio lands as a WAV under <c>workspace/artifacts/gemini-tts/</c>.
/// </summary>
public sealed class GeminiTextToSpeechSkillTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public GeminiTextToSpeechSkillTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-gemini-tts-" + Guid.NewGuid().ToString("N"));
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
    public async Task ExecuteAsync_HappyPath_WritesWavArtifactAndPostsExpectedJson()
    {
        // 80 bytes of dummy PCM — content irrelevant, but enough to verify base64 round-trip
        // and a non-trivial WAV file size assertion.
        var pcm = new byte[80];
        for (var i = 0; i < pcm.Length; i++) { pcm[i] = (byte)(i & 0xFF); }
        var pcmB64 = Convert.ToBase64String(pcm);

        var responseBody = JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new[]
                        {
                            new { inlineData = new { mimeType = "audio/L16;rate=24000", data = pcmB64 } },
                        },
                    },
                },
            },
        });

        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });

        var skill = new GeminiTextToSpeechSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [GeminiTextToSpeechSkill.ApiKeyEnvVar] = "fake-key" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hello world" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var artifactPath = Assert.Single(result.Artifacts);
        Assert.True(File.Exists(artifactPath), $"expected artifact at {artifactPath}");

        // 44-byte WAV header + 80 bytes PCM.
        var bytes = await File.ReadAllBytesAsync(artifactPath);
        Assert.Equal(44 + pcm.Length, bytes.Length);
        Assert.Equal((byte)'R', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal((byte)'F', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
        // Sample rate at offset 24 (little-endian 24_000 = 0x00005DC0).
        Assert.Equal(24_000, BitConverter.ToInt32(bytes, 24));

        // Single request was made to the configured endpoint with the configured model + key.
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.NotNull(request.RequestUri);
        Assert.Contains("gemini-2.5-flash-preview-tts:generateContent", request.RequestUri!.AbsoluteUri);
        Assert.Contains("key=fake-key", request.RequestUri.Query);

        // Body contains the requested text and voice.
        var sentBody = request.BodyAsString();
        Assert.Contains("hello world", sentBody);
        Assert.Contains("\"voiceName\":\"Kore\"", sentBody);
    }

    [Fact]
    public async Task ExecuteAsync_MissingApiKey_FailsCleanlyWithoutHttp()
    {
        var handler = new FakeHttpMessageHandler();
        var skill = new GeminiTextToSpeechSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string>()); // no GEMINI_API_KEY

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hi" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(GeminiTextToSpeechSkill.ApiKeyEnvVar, result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_MissingText_FailsCleanlyWithoutHttp()
    {
        var handler = new FakeHttpMessageHandler();
        var skill = new GeminiTextToSpeechSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [GeminiTextToSpeechSkill.ApiKeyEnvVar] = "fake-key" });

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
        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"API key invalid\"}}", Encoding.UTF8, "application/json"),
        });

        var skill = new GeminiTextToSpeechSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [GeminiTextToSpeechSkill.ApiKeyEnvVar] = "bad-key" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hi" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("400", result.Error);
        Assert.Contains("API key invalid", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ResponseWithNoAudio_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"candidates\":[]}", Encoding.UTF8, "application/json"),
        });

        var skill = new GeminiTextToSpeechSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [GeminiTextToSpeechSkill.ApiKeyEnvVar] = "fake-key" });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "hi" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no audio", result.Error);
    }

    [Fact]
    public void DefaultManifest_HasExpectedShape()
    {
        var m = GeminiTextToSpeechSkill.DefaultManifest();
        Assert.Equal("gemini-tts", m.Id);
        Assert.Equal(SkillRuntime.Managed, m.Runtime);
        Assert.Equal("Sutando.Skills.Cloud.Google.GeminiTextToSpeechSkill, Sutando.Skills.Cloud", m.Entry);
        Assert.Contains("http-out", m.Capabilities);
        Assert.Contains("audio", m.Capabilities);
        Assert.Contains("gemini-tts", m.Triggers);
    }

    [SkippableFact]
    public async Task LiveApi_RoundTripsToActualGemini()
    {
        // Opt-in live API check: only runs when GEMINI_API_KEY is set in the process environment.
        // Mirrors the pattern of GeminiLiveIntegrationTests — useful for manual verification
        // before publishing a new release; never gates CI.
        var apiKey = Environment.GetEnvironmentVariable(GeminiTextToSpeechSkill.ApiKeyEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(apiKey), $"{GeminiTextToSpeechSkill.ApiKeyEnvVar} not set; skipping live-API round-trip");

        var skill = new GeminiTextToSpeechSkill();
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(),
            env: new Dictionary<string, string> { [GeminiTextToSpeechSkill.ApiKeyEnvVar] = apiKey! });

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["text"] = "Hello from the sutando port." },
            CancellationToken.None);

        // Environmental failures (rate limit, transient 5xx, network) are out of this test's
        // scope — they describe the operator's account, not the skill code. Skip on those so a
        // developer running the suite locally with an exhausted free tier doesn't see a red
        // failure that isn't theirs. Hard failures (4xx other than 429, malformed responses)
        // still fall through to the assertion below and surface as red.
        if (!result.Success && (result.Error.Contains("HTTP 429", StringComparison.Ordinal)
            || result.Error.Contains("HTTP 503", StringComparison.Ordinal)
            || result.Error.Contains("HTTP 500", StringComparison.Ordinal)))
        {
            Skip.If(true, $"Gemini API rate-limited / unavailable ({result.Error}); skipping");
        }

        Assert.True(result.Success, result.Error);
        var path = Assert.Single(result.Artifacts);
        var size = new FileInfo(path).Length;
        Assert.True(size > 44, $"WAV artifact at {path} is suspiciously small ({size} bytes — only the header?)");
    }
}
