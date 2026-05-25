using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sutando.Skills.Cloud.Common;

namespace Sutando.Skills.Cloud.Google;

/// <summary>
/// Cloud TTS via Google Gemini's <c>generateContent</c> endpoint in audio modality.
/// Synthesises the supplied <c>text</c> argument with the supplied <c>voice</c> (default
/// <c>Kore</c>) and writes the resulting 24 kHz mono 16-bit PCM as a WAV file under
/// <c>&lt;workspace&gt;/artifacts/gemini-tts/</c>. The artifact path is returned both as the
/// result body and in <see cref="SkillResult.Artifacts"/>.
/// </summary>
/// <remarks>
/// <para>
/// Endpoint: <c>POST https://generativelanguage.googleapis.com/v1beta/models/&lt;model&gt;:generateContent?key=&lt;GEMINI_API_KEY&gt;</c>.
/// Requires <c>GEMINI_API_KEY</c> in the skill context's environment.
/// </para>
/// <para>
/// Arguments:
/// <list type="bullet">
///   <item><term><c>text</c></term><description>Required. The text to synthesise.</description></item>
///   <item><term><c>voice</c></term><description>Optional. Prebuilt voice name; defaults to <c>Kore</c>.</description></item>
///   <item><term><c>model</c></term><description>Optional. Model id; defaults to <c>gemini-2.5-flash-preview-tts</c>.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class GeminiTextToSpeechSkill : ISkill
{
    /// <summary>Env var carrying the Gemini API key.</summary>
    public const string ApiKeyEnvVar = "GEMINI_API_KEY";

    /// <summary>Default TTS model when the caller doesn't override.</summary>
    public const string DefaultModel = "gemini-2.5-flash-preview-tts";

    /// <summary>Default voice when the caller doesn't override.</summary>
    public const string DefaultVoice = "Kore";

    private const int SampleRateHz = 24_000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <summary>Construct with the default manifest.</summary>
    public GeminiTextToSpeechSkill() : this(DefaultManifest()) { }

    /// <summary>Construct with a caller-supplied manifest (used by the managed-factory path).</summary>
    public GeminiTextToSpeechSkill(SkillManifest manifest) => Manifest = manifest;

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
            return SkillResult.Fail("gemini-tts: 'text' argument is required", sw.Elapsed);
        }

        if (!context.Environment.TryGetValue(ApiKeyEnvVar, out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            sw.Stop();
            return SkillResult.Fail($"gemini-tts: missing env var '{ApiKeyEnvVar}'", sw.Elapsed);
        }

        var voice = arguments.TryGetValue("voice", out var v) && !string.IsNullOrWhiteSpace(v) ? v : DefaultVoice;
        var model = arguments.TryGetValue("model", out var m) && !string.IsNullOrWhiteSpace(m) ? m : DefaultModel;

        var request = new GenerateContentRequest
        {
            Contents = [new RequestContent { Parts = [new RequestPart { Text = text }] }],
            GenerationConfig = new GenerationConfig
            {
                ResponseModalities = ["AUDIO"],
                SpeechConfig = new SpeechConfig
                {
                    VoiceConfig = new VoiceConfig
                    {
                        PrebuiltVoiceConfig = new PrebuiltVoiceConfig { VoiceName = voice },
                    },
                },
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
            return SkillResult.Fail($"gemini-tts: HTTP request failed: {ex.Message}", sw.Elapsed);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
            sw.Stop();
            return SkillResult.Fail(
                $"gemini-tts: HTTP {(int)response.StatusCode}: {Truncate(body, 200)}",
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
            return SkillResult.Fail($"gemini-tts: response was not valid JSON: {ex.Message}", sw.Elapsed);
        }

        var inlineB64 = parsed?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.InlineData?.Data;
        if (string.IsNullOrEmpty(inlineB64))
        {
            sw.Stop();
            return SkillResult.Fail("gemini-tts: response contained no audio data", sw.Elapsed);
        }

        byte[] pcm;
        try
        {
            pcm = Convert.FromBase64String(inlineB64);
        }
        catch (FormatException ex)
        {
            sw.Stop();
            return SkillResult.Fail($"gemini-tts: response audio not valid base64: {ex.Message}", sw.Elapsed);
        }

        var wav = WrapAsWav(pcm, SampleRateHz, Channels, BitsPerSample);
        var path = await ArtifactWriter.WriteAsync(context.Workspace, Manifest.Id, wav, "wav", ct).ConfigureAwait(false);

        sw.Stop();
        return SkillResult.Ok(
            body: $"Synthesised {pcm.Length:N0} bytes of PCM ({wav.Length:N0} bytes WAV) at {path}",
            duration: sw.Elapsed,
            artifacts: [path]);
    }

    /// <summary>Canonical manifest for the gemini-tts skill — used by both built-in and on-disk variants.</summary>
    public static SkillManifest DefaultManifest() => new()
    {
        Id = "gemini-tts",
        Name = "Gemini Text-to-Speech",
        Description = "Synthesise speech via Google Gemini's audio-modality generateContent endpoint. Writes a 24 kHz mono WAV to the workspace artifacts dir.",
        Version = "0.1.0",
        Runtime = SkillRuntime.Managed,
        Entry = "Sutando.Skills.Cloud.Google.GeminiTextToSpeechSkill, Sutando.Skills.Cloud",
        Triggers = ["gemini-tts", "tts:gemini", "speak:gemini"],
        Capabilities = ["http-out", "fs-write", "audio"],
    };

    private static byte[] WrapAsWav(ReadOnlySpan<byte> pcm, int sampleRate, int channels, int bitsPerSample)
    {
        // Minimal 44-byte canonical WAV header for PCM. Gemini's audio modality returns raw
        // little-endian signed 16-bit PCM with no container, so we have to wrap it ourselves
        // for downstream tools (ffmpeg, audio editors) to recognise the file.
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var dataSize = pcm.Length;
        var fileSize = 36 + dataSize;

        var output = new byte[44 + dataSize];
        var span = output.AsSpan();

        "RIFF"u8.CopyTo(span[0..4]);
        BitConverter.TryWriteBytes(span[4..8], fileSize);
        "WAVE"u8.CopyTo(span[8..12]);
        "fmt "u8.CopyTo(span[12..16]);
        BitConverter.TryWriteBytes(span[16..20], 16);              // fmt chunk size
        BitConverter.TryWriteBytes(span[20..22], (short)1);        // PCM format
        BitConverter.TryWriteBytes(span[22..24], (short)channels);
        BitConverter.TryWriteBytes(span[24..28], sampleRate);
        BitConverter.TryWriteBytes(span[28..32], byteRate);
        BitConverter.TryWriteBytes(span[32..34], blockAlign);
        BitConverter.TryWriteBytes(span[34..36], (short)bitsPerSample);
        "data"u8.CopyTo(span[36..40]);
        BitConverter.TryWriteBytes(span[40..44], dataSize);
        pcm.CopyTo(span[44..]);

        return output;
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
        public required SpeechConfig SpeechConfig { get; init; }
    }

    private sealed record SpeechConfig
    {
        public required VoiceConfig VoiceConfig { get; init; }
    }

    private sealed record VoiceConfig
    {
        public required PrebuiltVoiceConfig PrebuiltVoiceConfig { get; init; }
    }

    private sealed record PrebuiltVoiceConfig
    {
        public required string VoiceName { get; init; }
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
    }

    private sealed record InlineData
    {
        [JsonPropertyName("mimeType")] public string? MimeType { get; init; }
        [JsonPropertyName("data")] public string? Data { get; init; }
    }
}
