namespace Sutando.Channels.Telegram;

/// <summary>
/// Minimal seam over the Telegram Bot API. The real implementation is
/// <see cref="TelegramBotGateway"/> which wraps <c>Telegram.Bot.ITelegramBotClient</c>;
/// tests provide a hand-written fake to drive <see cref="TelegramChannel"/> offline.
/// </summary>
/// <remarks>
/// <para>
/// The gateway intentionally exposes only the operations the channel uses. This keeps the
/// blast radius of a Telegram.Bot SDK upgrade small: any v22.x → vNext rename only has to
/// be reconciled inside <see cref="TelegramBotGateway"/>.
/// </para>
/// <para>
/// Updates are surfaced as the lightweight <see cref="TelegramInboundUpdate"/> record so
/// the channel doesn't need to reason about the full Telegram.Bot DTO surface.
/// </para>
/// </remarks>
public interface ITelegramGateway
{
    /// <summary>
    /// Long-poll for updates newer than <paramref name="offset"/>. Returns immediately if
    /// updates are available; otherwise blocks up to <paramref name="timeout"/> at Telegram's edge.
    /// </summary>
    /// <param name="offset">Server-side update offset; <see langword="null"/> on the first call.</param>
    /// <param name="timeout">Long-poll wait time at the Telegram edge. Telegram caps this at 50 s.</param>
    /// <param name="ct">Cancellation token tied to the channel run loop.</param>
    /// <returns>The updates in delivery order. May be empty.</returns>
    Task<IReadOnlyList<TelegramInboundUpdate>> GetUpdatesAsync(int? offset, TimeSpan timeout, CancellationToken ct);

    /// <summary>Send a text message to the given chat. Honours Telegram's 4096-char per-message ceiling — caller chunks.</summary>
    /// <param name="chatId">Telegram chat identifier.</param>
    /// <param name="text">Body text; must be ≤ 4096 chars.</param>
    /// <param name="replyToMessageId">Optional inbound message id to thread the reply against.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendTextAsync(long chatId, string text, int? replyToMessageId, CancellationToken ct);

    /// <summary>Upload a file as a Telegram document.</summary>
    /// <param name="chatId">Telegram chat identifier.</param>
    /// <param name="path">Absolute path to the local file.</param>
    /// <param name="caption">Optional caption attached to the document.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendDocumentAsync(long chatId, string path, string? caption, CancellationToken ct);

    /// <summary>
    /// Download a file referenced by Telegram <c>file_id</c> to the given local path. The
    /// path's parent directory is created if necessary.
    /// </summary>
    /// <param name="fileId">Telegram-server file identifier.</param>
    /// <param name="destinationPath">Absolute destination path.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DownloadFileAsync(string fileId, string destinationPath, CancellationToken ct);
}

/// <summary>
/// Sutando's compact, SDK-independent view of an inbound Telegram update. Only the fields
/// <see cref="TelegramChannel"/> needs to dispatch are present.
/// </summary>
public sealed record TelegramInboundUpdate
{
    /// <summary>Telegram update identifier — monotonically increasing per bot.</summary>
    public required int UpdateId { get; init; }

    /// <summary>Telegram message id within the originating chat.</summary>
    public required int MessageId { get; init; }

    /// <summary>Originating chat id (negative for groups / channels).</summary>
    public required long ChatId { get; init; }

    /// <summary>Originating user id; <see langword="null"/> for messages without a sender (anonymous channel posts).</summary>
    public required long? FromUserId { get; init; }

    /// <summary>Originating user display name (first name preferred, falls back to username); empty if unknown.</summary>
    public string FromDisplayName { get; init; } = string.Empty;

    /// <summary>Message timestamp (Telegram server time).</summary>
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>Text body or caption; empty for media-only messages without a caption.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Photo file id (largest variant) when the message contains a photo; <see langword="null"/> otherwise.</summary>
    public string? PhotoFileId { get; init; }

    /// <summary>Voice-note file id when the message contains a voice note.</summary>
    public string? VoiceFileId { get; init; }

    /// <summary>Document file id when the message contains a generic document.</summary>
    public string? DocumentFileId { get; init; }

    /// <summary>Suggested filename for a document (Telegram-supplied) — used as the on-disk filename.</summary>
    public string? DocumentFileName { get; init; }
}
