using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Sutando.Skills;
using Sutando.Skills.Cloud.Google;
using Sutando.Skills.Cloud.Orchestration;
using Sutando.Workspace;

namespace Sutando.Tests.Skills.Cloud;

/// <summary>
/// Fake-HTTP + subprocess coverage for <see cref="MakeViralVideoSkill"/>.
/// The happy-path test is marked <see cref="SkippableFactAttribute"/> because it requires
/// ffmpeg on PATH; all validation/failure tests run unconditionally.
/// </summary>
public sealed class MakeViralVideoSkillTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public MakeViralVideoSkillTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-viral-video-" + Guid.NewGuid().ToString("N"));
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

    // -----------------------------------------------------------------------
    // Happy path (requires ffmpeg)
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task ExecuteAsync_TwoPrompts_ProducesMp4WithCorrectArtifact()
    {
        Skip.If(!ToolAvailable("ffmpeg"), "ffmpeg not on PATH; skipping render test");

        // Stub two image-generation HTTP responses — one per prompt.
        var pngBytes = MinimalPng();
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(OkJsonResponse(ImageGenResponseBody("image/png", Convert.ToBase64String(pngBytes))))
            .EnqueueResponse(OkJsonResponse(ImageGenResponseBody("image/png", Convert.ToBase64String(pngBytes))));

        var skill = BuildSkill(handler);
        var ctx = ContextWith(handler);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string>
            {
                ["prompts"] = "a sunrise over mountains|a starry night",
                ["seconds_per_frame"] = "1",
            },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);

        // Only the mp4 is in Artifacts; intermediate images live under image-generation/.
        var mp4Path = Assert.Single(result.Artifacts);
        Assert.EndsWith(".mp4", mp4Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(mp4Path), $"Expected mp4 at {mp4Path}");
        Assert.True(new FileInfo(mp4Path).Length > 0, "mp4 should be non-empty");

        Assert.Contains("2-frame slideshow", result.Body);
        Assert.Contains("a sunrise over mountains", result.Body);
        Assert.Contains("a starry night", result.Body);

        // Both image requests were fired.
        Assert.Equal(2, handler.Requests.Count);
    }

    // -----------------------------------------------------------------------
    // Validation failures — all unconditional
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_MissingApiKey_FailsCleanlyWithoutHttp()
    {
        var handler = new FakeHttpMessageHandler();
        var skill = BuildSkill(handler);
        var ctx = new SkillContext(
            _workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string>());

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["prompts"] = "a sunset" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(GeminiImageGenerationSkill.ApiKeyEnvVar, result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_MissingPromptsArgument_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler();
        var skill = BuildSkill(handler);
        var ctx = ContextWith(handler);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'prompts'", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyPromptsAfterSplit_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler();
        var skill = BuildSkill(handler);
        var ctx = ContextWith(handler);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["prompts"] = "   |   |  " },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("at least one non-empty", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_MoreThanTenPrompts_FailsCleanly()
    {
        var handler = new FakeHttpMessageHandler();
        var skill = BuildSkill(handler);
        var ctx = ContextWith(handler);

        var tooManyPrompts = string.Join("|", Enumerable.Range(1, 11).Select(i => $"prompt {i}"));

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["prompts"] = tooManyPrompts },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("11", result.Error);
        Assert.Contains($"{MakeViralVideoSkill.MaxPrompts}", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_InnerImageGenerationFailure_SurfacesInnerError()
    {
        // Stub a 429 from the image-gen endpoint — skill should propagate the error message.
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"message\":\"quota exceeded\"}}", Encoding.UTF8, "application/json"),
            });

        var skill = BuildSkill(handler);
        var ctx = ContextWith(handler);

        var result = await skill.ExecuteAsync(
            ctx,
            new Dictionary<string, string> { ["prompts"] = "a mountain" },
            CancellationToken.None);

        Assert.False(result.Success);
        // The orchestrator should mention that image generation failed.
        Assert.Contains("image generation failed", result.Error, StringComparison.OrdinalIgnoreCase);
        // And surface the inner error (HTTP 429 from the image skill).
        Assert.Contains("429", result.Error);
    }

    // -----------------------------------------------------------------------
    // Manifest shape
    // -----------------------------------------------------------------------

    [Fact]
    public void DefaultManifest_HasExpectedShape()
    {
        var m = MakeViralVideoSkill.DefaultManifest();
        Assert.Equal("make-viral-video", m.Id);
        Assert.Equal(SkillRuntime.Managed, m.Runtime);
        Assert.Equal("Sutando.Skills.Cloud.Orchestration.MakeViralVideoSkill, Sutando.Skills.Cloud", m.Entry);
        Assert.Contains("http-out", m.Capabilities);
        Assert.Contains("video", m.Capabilities);
        Assert.Contains("image", m.Capabilities);
        Assert.Contains("fs-write", m.Capabilities);
        Assert.Contains("make-viral-video", m.Triggers);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Build a <see cref="MakeViralVideoSkill"/> with the default image-skill factory.
    /// HTTP calls from the inner <see cref="GeminiImageGenerationSkill"/> go through
    /// <see cref="SkillContext.Http"/> — which tests set to a <see cref="FakeHttpMessageHandler"/>
    /// via <see cref="ContextWith"/> — so no factory wiring is needed here.
    /// </summary>
    private static MakeViralVideoSkill BuildSkill(FakeHttpMessageHandler _) =>
        new(MakeViralVideoSkill.DefaultManifest(),
            imageSkillFactory: () => new GeminiImageGenerationSkill(),
            ffmpegPath: "ffmpeg");

    /// <summary>
    /// Create a <see cref="SkillContext"/> with <c>GEMINI_API_KEY</c> set and the
    /// given handler wired as the HTTP transport.
    /// </summary>
    private SkillContext ContextWith(FakeHttpMessageHandler handler) =>
        new(_workspace,
            skillRoot: _tempRoot,
            http: new HttpClient(handler),
            env: new Dictionary<string, string> { [GeminiImageGenerationSkill.ApiKeyEnvVar] = "fake-key" });

    private static string ImageGenResponseBody(string mimeType, string dataB64) =>
        JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new { content = new { parts = new[] { new { inlineData = new { mimeType, data = dataB64 } } } } },
            },
        });

    private static HttpResponseMessage OkJsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    /// <summary>
    /// Returns a 16×16 solid-red PNG. The 1×1 minimal PNG causes libx264 / yuv420p encoding
    /// to produce zero frames after the scale/pad filter; 16×16 is the smallest square that
    /// reliably encodes through the 1920×1080 pipeline used by <see cref="MakeViralVideoSkill"/>.
    /// </summary>
    private static byte[] MinimalPng()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAF0lEQVR4nGP4z8BAEiJN9aiGUQ1DSgMAkPn/Afnh+ngAAAAASUVORK5CYII=");
    }

    private static bool ToolAvailable(string tool)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "", ".exe", ".cmd", ".bat" }
            : new[] { string.Empty };

        foreach (var dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                if (File.Exists(Path.Combine(dir, tool + ext)))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
