using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sutando.Skills.Cloud.Google;

namespace Sutando.Skills.Cloud.Orchestration;

/// <summary>
/// Orchestration skill that generates N images via <see cref="GeminiImageGenerationSkill"/>,
/// then stitches them into a slideshow MP4 using an ffmpeg subprocess. Each prompt becomes
/// one frame; frames are held for a configurable number of seconds.
/// </summary>
/// <remarks>
/// <para>
/// Scope (Wave D, minimal port): slideshow only — no music, no captions, no transitions.
/// Those are deferred to a follow-up once there is a real consumer that defines requirements.
/// </para>
/// <para>
/// Requires <c>GEMINI_API_KEY</c> in the skill context's environment (for image generation).
/// Also requires ffmpeg on PATH at execute-time; absence produces a runtime
/// <see cref="SkillResult.Fail"/> rather than blocking registration.
/// </para>
/// <para>
/// Arguments:
/// <list type="bullet">
///   <item>
///     <term><c>prompts</c></term>
///     <description>Required. Pipe-separated image prompts, e.g.
///     <c>"a sunrise|a flowing river|a starry night"</c>. 1–10 prompts.</description>
///   </item>
///   <item>
///     <term><c>seconds_per_frame</c></term>
///     <description>Optional. Display time per frame in seconds; default <c>3</c>.</description>
///   </item>
///   <item>
///     <term><c>output</c></term>
///     <description>Optional. Output filename (basename only); default <c>viral.mp4</c>.
///     Placed under <c>workspace/artifacts/make-viral-video/&lt;timestamp&gt;-&lt;hash&gt;-&lt;filename&gt;</c>.</description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class MakeViralVideoSkill : ISkill
{
    /// <summary>Maximum number of prompts (frames) accepted per invocation.</summary>
    public const int MaxPrompts = 10;

    /// <summary>Default display duration per frame when the caller doesn't override.</summary>
    public const int DefaultSecondsPerFrame = 3;

    /// <summary>Default output filename when the caller doesn't override.</summary>
    public const string DefaultOutputFilename = "viral.mp4";

    /// <summary>ffmpeg video filter applied to every frame: letterbox/pillarbox to 1920×1080.</summary>
    private const string VideoFilter =
        "scale=1920:1080:force_original_aspect_ratio=decrease," +
        "pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1";

    private readonly Func<GeminiImageGenerationSkill> _imageSkillFactory;
    private readonly string _ffmpegPath;

    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <summary>Construct with the default manifest and production defaults.</summary>
    public MakeViralVideoSkill() : this(DefaultManifest()) { }

    /// <summary>Construct with a caller-supplied manifest and production defaults.</summary>
    public MakeViralVideoSkill(SkillManifest manifest)
        : this(manifest, () => new GeminiImageGenerationSkill(), "ffmpeg") { }

    /// <summary>
    /// Construct with injected factory and ffmpeg path — used by tests to substitute fakes.
    /// </summary>
    /// <param name="manifest">Manifest to expose on <see cref="Manifest"/>.</param>
    /// <param name="imageSkillFactory">Factory that returns an image-generation skill per invocation.</param>
    /// <param name="ffmpegPath">Binary name or absolute path for ffmpeg; probed at execute-time.</param>
    public MakeViralVideoSkill(
        SkillManifest manifest,
        Func<GeminiImageGenerationSkill> imageSkillFactory,
        string ffmpegPath)
    {
        Manifest = manifest;
        _imageSkillFactory = imageSkillFactory;
        _ffmpegPath = ffmpegPath;
    }

    /// <inheritdoc/>
    public async Task<SkillResult> ExecuteAsync(
        SkillContext context,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arguments);

        var sw = Stopwatch.StartNew();

        // --- Validate: prompts argument ---
        if (!arguments.TryGetValue("prompts", out var promptsRaw) || string.IsNullOrWhiteSpace(promptsRaw))
        {
            sw.Stop();
            return SkillResult.Fail("make-viral-video: 'prompts' argument is required", sw.Elapsed);
        }

        var prompts = promptsRaw.Split('|', StringSplitOptions.TrimEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (prompts.Count == 0)
        {
            sw.Stop();
            return SkillResult.Fail("make-viral-video: 'prompts' must contain at least one non-empty segment", sw.Elapsed);
        }

        if (prompts.Count > MaxPrompts)
        {
            sw.Stop();
            return SkillResult.Fail(
                $"make-viral-video: too many prompts ({prompts.Count}); maximum is {MaxPrompts}",
                sw.Elapsed);
        }

        // --- Validate: GEMINI_API_KEY ---
        if (!context.Environment.TryGetValue(GeminiImageGenerationSkill.ApiKeyEnvVar, out var apiKey)
            || string.IsNullOrWhiteSpace(apiKey))
        {
            sw.Stop();
            return SkillResult.Fail(
                $"make-viral-video: missing env var '{GeminiImageGenerationSkill.ApiKeyEnvVar}'",
                sw.Elapsed);
        }

        // --- Parse optional arguments ---
        var secondsPerFrame = DefaultSecondsPerFrame;
        if (arguments.TryGetValue("seconds_per_frame", out var spfRaw)
            && int.TryParse(spfRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var spfParsed)
            && spfParsed > 0)
        {
            secondsPerFrame = spfParsed;
        }

        var outputFilename = arguments.TryGetValue("output", out var outRaw) && !string.IsNullOrWhiteSpace(outRaw)
            ? Path.GetFileName(outRaw)   // strip any caller-supplied directory components
            : DefaultOutputFilename;

        // --- Generate images ---
        var imageSkill = _imageSkillFactory();
        var imagePaths = new List<string>(prompts.Count);

        for (var i = 0; i < prompts.Count; i++)
        {
            var imageArgs = new Dictionary<string, string> { ["prompt"] = prompts[i] };
            SkillResult imageResult;
            try
            {
                // Pass the same context so tests can inject HttpClient via FakeHttpMessageHandler.
                imageResult = await imageSkill.ExecuteAsync(context, imageArgs, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return SkillResult.Fail(
                    $"make-viral-video: image generation threw for prompt {i + 1}: {ex.Message}",
                    sw.Elapsed);
            }

            if (!imageResult.Success)
            {
                sw.Stop();
                return SkillResult.Fail(
                    $"make-viral-video: image generation failed for prompt {i + 1} (\"{prompts[i]}\"): {imageResult.Error}",
                    sw.Elapsed);
            }

            // Take the first artifact; image-generation returns one file per call here.
            if (imageResult.Artifacts.Count == 0)
            {
                sw.Stop();
                return SkillResult.Fail(
                    $"make-viral-video: image generation returned no artifacts for prompt {i + 1}",
                    sw.Elapsed);
            }

            imagePaths.Add(imageResult.Artifacts[0]);
        }

        // --- Build output path ---
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
        var shortHash = ShortHash(Encoding.UTF8.GetBytes(string.Join("|", prompts)));
        var artifactsDir = Path.Combine(context.Workspace.Root.FullName, "artifacts", Manifest.Id);
        Directory.CreateDirectory(artifactsDir);
        var outputPath = Path.Combine(artifactsDir, $"{stamp}-{shortHash}-{outputFilename}");

        // --- Probe ffmpeg availability (done after image gen so image-gen-failure tests work
        //     without ffmpeg on PATH; the happy-path test is gated on ffmpeg via [SkippableFact]) ---
        if (!TryProbeFFmpeg(_ffmpegPath, out var probeError))
        {
            sw.Stop();
            return SkillResult.Fail(probeError!, sw.Elapsed);
        }

        // --- Write ffmpeg concat list ---
        var listPath = Path.Combine(Path.GetTempPath(), $"sutando-viral-{Guid.NewGuid():N}.txt");
        try
        {
            await WriteConcatListAsync(listPath, imagePaths, secondsPerFrame, ct).ConfigureAwait(false);

            // --- Run ffmpeg ---
            var (exitCode, stderr) = await RunFFmpegAsync(_ffmpegPath, listPath, outputPath, ct).ConfigureAwait(false);

            if (exitCode != 0)
            {
                sw.Stop();
                var errorSnippet = stderr.Length > 200 ? stderr[^200..] : stderr;
                return SkillResult.Fail(
                    $"make-viral-video: ffmpeg exited {exitCode}: {errorSnippet}",
                    sw.Elapsed);
            }
        }
        finally
        {
            // Clean up temp list file regardless of success/failure.
            try { File.Delete(listPath); } catch (IOException) { }
        }

        var mp4Size = new FileInfo(outputPath).Length;
        var imageList = string.Join(", ", imagePaths);
        var promptSummary = string.Join(" | ", prompts);

        sw.Stop();
        return SkillResult.Ok(
            body: $"Generated {prompts.Count}-frame slideshow at {outputPath} ({mp4Size:N0} bytes) " +
                  $"from prompts: {promptSummary}\n" +
                  $"Intermediate images: {imageList}",
            duration: sw.Elapsed,
            artifacts: [outputPath]);
    }

    /// <summary>Canonical manifest for the make-viral-video skill.</summary>
    public static SkillManifest DefaultManifest() => new()
    {
        Id = "make-viral-video",
        Name = "Make Viral Video",
        Description = "Generate N images via Gemini image-generation and stitch them into a slideshow MP4 using ffmpeg.",
        Version = "0.1.0",
        Runtime = SkillRuntime.Managed,
        Entry = "Sutando.Skills.Cloud.Orchestration.MakeViralVideoSkill, Sutando.Skills.Cloud",
        Triggers = ["make-viral-video", "viral-video", "slideshow-video"],
        Capabilities = ["http-out", "fs-write", "image", "video"],
    };

    /// <summary>
    /// Probe ffmpeg by running <c>ffmpeg -version</c>. Returns false and sets
    /// <paramref name="error"/> if not found or not executable.
    /// </summary>
    private static bool TryProbeFFmpeg(string ffmpegPath, out string? error)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3_000);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            error = $"make-viral-video: ffmpeg not found on PATH ('{ffmpegPath}'). " +
                    "Install ffmpeg and ensure it is on PATH before invoking this skill.";
            return false;
        }
    }

    /// <summary>
    /// Write an ffmpeg concat-demuxer list to <paramref name="listPath"/>.
    /// The last file entry is repeated without a duration line so the concat demuxer
    /// preserves the final frame's display time correctly.
    /// </summary>
    private static async Task WriteConcatListAsync(
        string listPath,
        IReadOnlyList<string> imagePaths,
        int secondsPerFrame,
        CancellationToken ct)
    {
        var sb = new StringBuilder();

        // ffconcat version header is optional but makes the format unambiguous.
        sb.Append("ffconcat version 1.0\n");

        for (var i = 0; i < imagePaths.Count; i++)
        {
            // Forward slashes are portable for ffmpeg even on Windows; backslashes can be
            // misinterpreted as escape characters in the concat list format.
            var fwd = imagePaths[i].Replace('\\', '/');

            // Escape any single-quote characters in the path (concat-demuxer quoting style).
            var escaped = fwd.Replace("'", @"'\''");

            // Use LF-only line endings — the concat demuxer may reject CRLF on Windows ffmpeg builds.
            sb.Append(CultureInfo.InvariantCulture, $"file '{escaped}'\n");
            sb.Append(CultureInfo.InvariantCulture, $"duration {secondsPerFrame}\n");
        }

        // Repeat the last entry without a duration — the concat demuxer drops the duration
        // of the final file, so we add an extra reference to carry the last frame through.
        if (imagePaths.Count > 0)
        {
            var lastFwd = imagePaths[^1].Replace('\\', '/');
            var lastEscaped = lastFwd.Replace("'", @"'\''");
            sb.Append(CultureInfo.InvariantCulture, $"file '{lastEscaped}'\n");
        }

        // UTF-8 without BOM — the BOM would corrupt the 'ffconcat version 1.0' header and make
        // the concat demuxer reject the file with "Invalid data found when processing input".
        await File.WriteAllTextAsync(listPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Run ffmpeg to produce an MP4 from the concat list. Returns (exitCode, stderr).
    /// </summary>
    private static async Task<(int ExitCode, string Stderr)> RunFFmpegAsync(
        string ffmpegPath,
        string listPath,
        string outputPath,
        CancellationToken ct)
    {
        // Use ArgumentList so each argument is passed without shell quoting worries —
        // the runtime builds the correct OS-level command line for us.
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("concat");
        psi.ArgumentList.Add("-safe");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(listPath);
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add(VideoFilter);
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read stderr asynchronously to avoid deadlocking when the output buffer fills.
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false); // drain stdout to avoid pipe stall

        return (process.ExitCode, stderr);
    }

    private static string ShortHash(ReadOnlySpan<byte> payload)
    {
        // Mirrors ArtifactWriter's approach: SHA-256, first 4 bytes as lowercase hex.
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        return Convert.ToHexString(hash[..4]).ToLowerInvariant();
    }
}
