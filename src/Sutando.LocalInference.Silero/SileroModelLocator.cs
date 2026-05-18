using System.Diagnostics.CodeAnalysis;

namespace Sutando.LocalInference.Silero;

/// <summary>
/// Helper that resolves the Silero VAD v5 ONNX model file by downloading it on first use
/// into a stable cache directory.
/// </summary>
/// <remarks>
/// <para>
/// The Silero VAD model is MIT-licensed and small (~2.2 MB), which makes it a reasonable
/// "fetch on first run" dependency. We deliberately do <b>not</b> embed it in the NuGet —
/// the LLM ecosystem has been burned by binary-in-package surprises (size, supply-chain,
/// licence-review) — but provide a zero-friction download fallback so consumers who don't
/// want to manage the file can call
/// <see cref="SileroServiceCollectionExtensions.AddSileroVadAutoDownload"/> and get back to
/// productive work.
/// </para>
/// <para>
/// Source: <c>https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx</c>.
/// Cache location: <c>%LOCALAPPDATA%/sutando/local-inference/silero_vad.onnx</c> on Windows;
/// <c>~/.local/share/sutando/local-inference/silero_vad.onnx</c> on Linux / macOS.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public static class SileroModelLocator
{
    /// <summary>Canonical Silero v5 ONNX model URL on GitHub.</summary>
    public const string ModelUrl = "https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx";

    /// <summary>Default cache file name (no path) under the resolved cache directory.</summary>
    public const string DefaultFileName = "silero_vad.onnx";

    /// <summary>
    /// Resolve the path of the locally-cached Silero ONNX model, downloading it on first call
    /// if it doesn't already exist.
    /// </summary>
    /// <param name="cancellationToken">Cancellation. Honoured during the download.</param>
    /// <returns>Absolute path to the model file on disk.</returns>
    /// <exception cref="HttpRequestException">The HTTP download fails (network / server error).</exception>
    public static async Task<string> EnsureModelAsync(CancellationToken cancellationToken = default)
    {
        var cacheDir = ResolveCacheDirectory();
        var modelPath = Path.Combine(cacheDir, DefaultFileName);

        if (File.Exists(modelPath))
        {
            return modelPath;
        }

        Directory.CreateDirectory(cacheDir);

        // Atomic-ish placement: download to a sibling .partial then File.Move into place. This
        // prevents partial-on-disk-but-looks-complete failure modes if the process is killed
        // mid-download — the next run will see no canonical file and re-download.
        var partialPath = modelPath + ".partial";

        using (var http = new HttpClient())
        using (var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var dst = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
        }

        // File.Move(overwrite: true) handles a rare race where two processes complete the
        // download simultaneously — the loser wins idempotently with identical content.
        File.Move(partialPath, modelPath, overwrite: true);
        return modelPath;
    }

    /// <summary>
    /// Resolve the directory under which we cache downloaded local-inference assets, creating
    /// it lazily. Roams under <see cref="Environment.SpecialFolder.LocalApplicationData"/> so
    /// the cache survives across user sessions on every platform.
    /// </summary>
    public static string ResolveCacheDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            // LocalApplicationData is empty on some headless / container environments —
            // fall back to a temp-dir subfolder so the download still works there.
            root = Path.Combine(Path.GetTempPath(), "sutando-cache");
        }

        return Path.Combine(root, "sutando", "local-inference");
    }
}
