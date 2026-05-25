using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sutando.Skills.Cloud.Common;

namespace Sutando.Skills.Cloud.Google;

/// <summary>
/// Cloud image generation via Google Gemini's <c>generateContent</c> endpoint in image
/// modality. Sends the supplied <c>prompt</c> and writes the returned inline image bytes
/// (PNG by default, occasionally JPEG depending on the model) to
/// <c>&lt;workspace&gt;/artifacts/image-generation/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Endpoint: <c>POST https://generativelanguage.googleapis.com/v1beta/models/&lt;model&gt;:generateContent?key=&lt;GEMINI_API_KEY&gt;</c>.
/// Requires <c>GEMINI_API_KEY</c> in the skill context's environment (shared with
/// <see cref="GeminiTextToSpeechSkill"/>).
/// </para>
/// <para>
/// Arguments:
/// <list type="bullet">
///   <item><term><c>prompt</c></term><description>Required. The image prompt.</description></item>
///   <item><term><c>model</c></term><description>Optional. Model id; defaults to <c>gemini-2.0-flash-preview-image-generation</c>.</description></item>
/// </list>
/// </para>
/// <para>
/// Models that return multiple candidates or multiple image parts produce one artifact per
/// inline-data part — <see cref="SkillResult.Artifacts"/> contains every written file in
/// response order. Non-image parts (text rationale, etc.) are joined into
/// <see cref="SkillResult.Body"/>.
/// </para>
/// </remarks>
public sealed class GeminiImageGenerationSkill : ISkill
{
    /// <summary>Env var carrying the Gemini API key.</summary>
    public const string ApiKeyEnvVar = "GEMINI_API_KEY";

    /// <summary>Default image-generation model when the caller doesn't override.</summary>
    public const string DefaultModel = "gemini-2.0-flash-preview-image-generation";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <summary>Construct with the default manifest.</summary>
    public GeminiImageGenerationSkill() : this(DefaultManifest()) { }

    /// <summary>Construct with a caller-supplied manifest (used by the managed-factory path).</summary>
    public GeminiImageGenerationSkill(SkillManifest manifest) => Manifest = manifest;

    /// <inheritdoc/>
    public async Task<SkillResult> ExecuteAsync(
        SkillContext context,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arguments);

        var sw = Stopwatch.StartNew();

        if (!arguments.TryGetValue("prompt", out var prompt) || string.IsNullOrWhiteSpace(prompt))
        {
            sw.Stop();
            return SkillResult.Fail("image-generation: 'prompt' argument is required", sw.Elapsed);
        }

        if (!context.Environment.TryGetValue(ApiKeyEnvVar, out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            sw.Stop();
            return SkillResult.Fail($"image-generation: missing env var '{ApiKeyEnvVar}'", sw.Elapsed);
        }

        var model = arguments.TryGetValue("model", out var m) && !string.IsNullOrWhiteSpace(m) ? m : DefaultModel;

        var request = new GenerateContentRequest
        {
            Contents = [new RequestContent { Parts = [new RequestPart { Text = prompt }] }],
            GenerationConfig = new GenerationConfig
            {
                // Both modalities so the model can return its image plus any textual rationale.
                // The wire format treats both as parts inside the same candidate — we sort them
                // out in the response loop below.
                ResponseModalities = ["IMAGE", "TEXT"],
            },
        };

        var uri = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        HttpResponseMessage response;
        try
        {
            response = await context.Http.PostAsJsonAsync(uri, request, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return SkillResult.Fail($"image-generation: HTTP request failed: {ex.Message}", sw.Elapsed);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await SafeReadAsync(response, ct).ConfigureAwait(false);
            sw.Stop();
            return SkillResult.Fail(
                $"image-generation: HTTP {(int)response.StatusCode}: {Truncate(errBody, 200)}",
                sw.Elapsed);
        }

        GenerateContentResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<GenerateContentResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            sw.Stop();
            return SkillResult.Fail($"image-generation: response was not valid JSON: {ex.Message}", sw.Elapsed);
        }

        var artifacts = new List<string>();
        var textBuf = new System.Text.StringBuilder();

        if (parsed?.Candidates is not null)
        {
            foreach (var candidate in parsed.Candidates)
            {
                if (candidate.Content?.Parts is null)
                {
                    continue;
                }
                foreach (var part in candidate.Content.Parts)
                {
                    if (part.InlineData is { Data: { Length: > 0 } b64, MimeType: var mime })
                    {
                        byte[] bytes;
                        try { bytes = Convert.FromBase64String(b64); }
                        catch (FormatException ex)
                        {
                            sw.Stop();
                            return SkillResult.Fail($"image-generation: response image not valid base64: {ex.Message}", sw.Elapsed);
                        }
                        var ext = ExtensionFor(mime);
                        var path = await ArtifactWriter.WriteAsync(context.Workspace, Manifest.Id, bytes, ext, ct).ConfigureAwait(false);
                        artifacts.Add(path);
                    }
                    else if (!string.IsNullOrEmpty(part.Text))
                    {
                        if (textBuf.Length > 0) { textBuf.Append('\n'); }
                        textBuf.Append(part.Text);
                    }
                }
            }
        }

        if (artifacts.Count == 0)
        {
            sw.Stop();
            return SkillResult.Fail("image-generation: response contained no inline image data", sw.Elapsed);
        }

        sw.Stop();
        var bodyText = textBuf.Length > 0 ? textBuf.ToString() + "\n" : string.Empty;
        var artifactList = string.Join(", ", artifacts);
        return SkillResult.Ok(
            body: $"{bodyText}Generated {artifacts.Count} image(s): {artifactList}",
            duration: sw.Elapsed,
            artifacts: artifacts);
    }

    /// <summary>Canonical manifest for the image-generation skill.</summary>
    public static SkillManifest DefaultManifest() => new()
    {
        Id = "image-generation",
        Name = "Gemini Image Generation",
        Description = "Generate an image from a text prompt via Google Gemini's image-modality generateContent endpoint.",
        Version = "0.1.0",
        Runtime = SkillRuntime.Managed,
        Entry = "Sutando.Skills.Cloud.Google.GeminiImageGenerationSkill, Sutando.Skills.Cloud",
        Triggers = ["image-generation", "generate-image", "image:gemini"],
        Capabilities = ["http-out", "fs-write", "image"],
    };

    private static string ExtensionFor(string? mimeType)
    {
        // mimeType examples from the API: "image/png", "image/jpeg", "image/webp". A missing or
        // unrecognised type falls back to ".bin" so the artifact is still preserved on disk —
        // the caller can sniff the magic bytes if they care.
        if (string.IsNullOrEmpty(mimeType)) { return "bin"; }
        var slash = mimeType.IndexOf('/');
        if (slash < 0) { return "bin"; }
        var sub = mimeType[(slash + 1)..].Split(';', 2)[0].Trim().ToLowerInvariant();
        return sub switch
        {
            "jpeg" => "jpg",
            "" => "bin",
            _ => sub,
        };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed record GenerateContentRequest
    {
        public required IReadOnlyList<RequestContent> Contents { get; init; }
        public required GenerationConfig GenerationConfig { get; init; }
    }

    private sealed record RequestContent
    {
        public required IReadOnlyList<RequestPart> Parts { get; init; }
    }

    private sealed record RequestPart
    {
        public required string Text { get; init; }
    }

    private sealed record GenerationConfig
    {
        public required IReadOnlyList<string> ResponseModalities { get; init; }
    }

    private sealed record GenerateContentResponse
    {
        [JsonPropertyName("candidates")] public IReadOnlyList<ResponseCandidate>? Candidates { get; init; }
    }

    private sealed record ResponseCandidate
    {
        [JsonPropertyName("content")] public ResponseContent? Content { get; init; }
    }

    private sealed record ResponseContent
    {
        [JsonPropertyName("parts")] public IReadOnlyList<ResponsePart>? Parts { get; init; }
    }

    private sealed record ResponsePart
    {
        [JsonPropertyName("inlineData")] public InlineData? InlineData { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
    }

    private sealed record InlineData
    {
        [JsonPropertyName("mimeType")] public string? MimeType { get; init; }
        [JsonPropertyName("data")] public string? Data { get; init; }
    }
}
