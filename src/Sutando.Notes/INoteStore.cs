using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sutando.Notes;

/// <summary>
/// Backing-store abstraction for a collection of notes. Concrete implementations are typically
/// filesystem-backed (<see cref="FileSystemNoteStore"/>) but the interface deliberately leaves
/// room for future variants — an in-memory store for tests, an SQLite-indexed store for
/// large libraries, or a remote sync-backed store.
/// </summary>
public interface INoteStore
{
    /// <summary>Absolute path of the directory the store is rooted at.</summary>
    string RootPath { get; }

    /// <summary>
    /// Stream every note in the store. Order is implementation-defined but stable within a
    /// single process. Notes that fail to parse are skipped silently — callers that want
    /// strict parsing should walk the filesystem themselves and call
    /// <see cref="ReadAsync"/> per path.
    /// </summary>
    IAsyncEnumerable<Note> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Read a single note by its store-relative path. Returns <see langword="null"/> when the
    /// note does not exist.
    /// </summary>
    /// <param name="relativePath">Path relative to <see cref="RootPath"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Note?> ReadAsync(string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Persist <paramref name="note"/> to disk atomically. Creates parent directories if
    /// missing. Overwrites any existing file at the target path.
    /// </summary>
    /// <param name="note">The note to write. <see cref="Note.Path"/> is the store-relative target.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteAsync(Note note, CancellationToken ct = default);

    /// <summary>
    /// Delete a note by its store-relative path. No-op when the file does not exist.
    /// </summary>
    /// <param name="relativePath">Path relative to <see cref="RootPath"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string relativePath, CancellationToken ct = default);
}
