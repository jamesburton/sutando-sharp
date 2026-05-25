using System.Security.Cryptography;
using Sutando.Workspace;

namespace Sutando.Skills.Cloud.Common;

/// <summary>
/// Helper that writes a binary artifact produced by a cloud skill (TTS audio, generated image,
/// generated video) to the workspace under a deterministic per-skill folder, returning the
/// absolute path so the skill can put it into <see cref="SkillResult.Artifacts"/>.
/// </summary>
/// <remarks>
/// <para>
/// Filenames follow the <c>&lt;utc-yyyyMMddTHHmmssfff&gt;-&lt;short-hash&gt;.&lt;ext&gt;</c>
/// convention so two concurrent invocations of the same skill never collide on disk even if
/// they happen in the same millisecond (the hash is derived from the payload bytes).
/// </para>
/// <para>
/// The output directory is <c>&lt;workspace&gt;/artifacts/&lt;skill-id&gt;/</c>. Created lazily
/// on first write — the workspace root is guaranteed to exist by <see cref="WorkspaceDirectory"/>.
/// </para>
/// </remarks>
public static class ArtifactWriter
{
    /// <summary>
    /// Persist <paramref name="payload"/> to the workspace's per-skill artifacts directory and
    /// return the resulting absolute path.
    /// </summary>
    /// <param name="workspace">Resolved workspace (provides the root directory).</param>
    /// <param name="skillId">Skill identifier — becomes a sub-folder name under <c>artifacts/</c>.</param>
    /// <param name="payload">The bytes to persist.</param>
    /// <param name="extension">File extension <em>without</em> the leading dot (e.g. <c>"mp3"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute filesystem path of the written artifact.</returns>
    public static async Task<string> WriteAsync(
        WorkspaceDirectory workspace,
        string skillId,
        ReadOnlyMemory<byte> payload,
        string extension,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var dir = Path.Combine(workspace.Root.FullName, "artifacts", skillId);
        Directory.CreateDirectory(dir);

        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var hash = ShortHash(payload.Span);
        var path = Path.Combine(dir, $"{stamp}-{hash}.{extension.TrimStart('.')}");

        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        return path;
    }

    private static string ShortHash(ReadOnlySpan<byte> payload)
    {
        // SHA-256 then take the first 8 hex chars — short enough to stay readable, wide enough
        // that ms-stamp + 32-bit hash collisions are vanishingly unlikely in practice.
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        return Convert.ToHexString(hash[..4]).ToLowerInvariant();
    }
}
