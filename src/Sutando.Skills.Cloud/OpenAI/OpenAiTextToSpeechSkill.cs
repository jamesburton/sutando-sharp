using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sutando.Skills.Cloud.Common;

namespace Sutando.Skills.Cloud.OpenAI;

/// <summary>
/// Cloud TTS via OpenAI's <c>/v1/audio/speech</c> endpoint. Synthesises the supplied
/// <c>text</c> argument with the supplied <c>voice</c> (default <c>alloy</c>), receives a
/// raw audio stream back (mp3 by default), and writes it to
/// <c>&lt;workspace&gt;/artifacts/openai-tts/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Endpoint: <c>POST https://api.openai.com/v1/audio/speech</c> with bearer auth.
/// Requires <c>OPENAI_API_KEY</c> in the skill context's environment.
/// </para>
/// <para>
/// Arguments:
/// <list type="bullet">
///   <item><term><c>text</c></term><description>Required. The text to synthesise (mapped to <c>input</c> in the wire body).</description></item>
///   <item><term><c>voice</c></term><description>Optional. One of OpenAI's prebuilt voices; defaults to <c>alloy</c>.</description></item>
///   <item><term><c>model</c></term><description>Optional. Model id; defaults to <c>tts-1</c>.</description></item>
///   <item><term><c>format</c></term><description>Optional. Output container — <c>mp3</c> (default), <c>opus</c>, <c>aac</c>, <c>flac</c>, <c>wav</c>, <c>pcm</c>.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class OpenAiTextToSpeechSkill : ISkill
{
    /// <summary>Env var carrying the OpenAI API key.</summary>
    public const string ApiKeyEnvVar = "OPENAI_API_KEY";

    /// <summary>Default model when the caller doesn't override.</summary>
    public const string DefaultModel = "tts-1";

    /// <summary>Default voice when the caller doesn't override.</summary>
    public const string DefaultVoice = "alloy";

    /// <summary>Default audio container when the caller doesn't override.</summary>
    public const string DefaultFormat = "mp3";

    private const string Endpoint = "https://api.openai.com/v1/audio/speech";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <summary>Construct with the default manifest.</summary>
    public OpenAiTextToSpeechSkill() : this(DefaultManifest()) { }

    /// <summary>Construct with a caller-supplied manifest (used by the managed-factory path).</summary>
    public OpenAiTextToSpeechSkill(SkillManifest manifest) => Manifest = manifest;

    /// <inheritdoc/>
    public async Task<SkillResult> ExecuteAsync(
        SkillContext context,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arguments);

        var sw = Stopwatch.StartNew();

        if (!arguments.TryGetValue("text", out var text) || string.IsNullOrWhiteSpace(text))
        {
            sw.Stop();
            return SkillResult.Fail("openai-tts: 'text' argument is required", sw.Elapsed);
        }

        if (!context.Environment.TryGetValue(ApiKeyEnvVar, out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            sw.Stop();
            return SkillResult.Fail($"openai-tts: missing env var '{ApiKeyEnvVar}'", sw.Elapsed);
        }

        var voice = arguments.TryGetValue("voice", out var v) && !string.IsNullOrWhiteSpace(v) ? v : DefaultVoice;
        var model = arguments.TryGetValue("model", out var m) && !string.IsNullOrWhiteSpace(m) ? m : DefaultModel;
        var format = arguments.TryGetValue("format", out var f) && !string.IsNullOrWhiteSpace(f) ? f : DefaultFormat;

        var body = new SpeechRequest
        {
            Model = model,
            Input = text,
            Voice = voice,
            ResponseFormat = format,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await context.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return SkillResult.Fail($"openai-tts: HTTP request failed: {ex.Message}", sw.Elapsed);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await SafeReadAsync(response, ct).ConfigureAwait(false);
            sw.Stop();
            return SkillResult.Fail(
                $"openai-tts: HTTP {(int)response.StatusCode}: {Truncate(errBody, 200)}",
                sw.Elapsed);
        }

        var audio = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (audio.Length == 0)
        {
            sw.Stop();
            return SkillResult.Fail("openai-tts: response body was empty", sw.Elapsed);
        }

        var ext = ExtensionFor(format);
        var path = await ArtifactWriter.WriteAsync(context.Workspace, Manifest.Id, audio, ext, ct).ConfigureAwait(false);

        sw.Stop();
        return SkillResult.Ok(
            body: $"Synthesised {audio.Length:N0} bytes of {format} audio at {path}",
            duration: sw.Elapsed,
            artifacts: [path]);
    }

    /// <summary>Canonical manifest for the openai-tts skill.</summary>
    public static SkillManifest DefaultManifest() => new()
    {
        Id = "openai-tts",
        Name = "OpenAI Text-to-Speech",
        Description = "Synthesise speech via OpenAI's /v1/audio/speech endpoint. Writes mp3 (or the requested format) to the workspace artifacts dir.",
        Version = "0.1.0",
        Runtime = SkillRuntime.Managed,
        Entry = "Sutando.Skills.Cloud.OpenAI.OpenAiTextToSpeechSkill, Sutando.Skills.Cloud",
        Triggers = ["openai-tts", "tts:openai", "speak:openai"],
        Capabilities = ["http-out", "fs-write", "audio"],
    };

    private static string ExtensionFor(string format) => format.ToLowerInvariant() switch
    {
        "mp3" => "mp3",
        "opus" => "opus",
        "aac" => "aac",
        "flac" => "flac",
        "wav" => "wav",
        "pcm" => "pcm",
        // Unknown formats: persist with the literal name as the extension so the caller can
        // still find the file. The OpenAI endpoint validates the format before returning, so
        // by the time we get here the format string is one of the supported set.
        _ => format,
    };

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

    private sealed record SpeechRequest
    {
        public required string Model { get; init; }
        public required string Input { get; init; }
        public required string Voice { get; init; }
        public required string ResponseFormat { get; init; }
    }
}
