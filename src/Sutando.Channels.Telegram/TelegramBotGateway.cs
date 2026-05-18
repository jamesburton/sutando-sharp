using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Sutando.Channels.Telegram;

/// <summary>
/// Production <see cref="ITelegramGateway"/> implementation backed by Telegram.Bot's
/// <c>ITelegramBotClient</c>. The class is intentionally thin — every method delegates
/// straight to the SDK so that the upgrade path on Telegram.Bot version bumps stays
/// localised here.
/// </summary>
internal sealed class TelegramBotGateway : ITelegramGateway
{
    private static readonly UpdateType[] AllowedUpdates =
    [
        UpdateType.Message,
        // Edited messages are intentionally ignored — upstream's behaviour. A retry/edit
        // would create stale tasks; users can resend instead.
    ];

    /// <summary>The maximum text length in a single Telegram message (Bot API limit).</summary>
    public const int TelegramTextLimit = 4096;

    private readonly ITelegramBotClient _client;

    /// <param name="client">Concrete Telegram bot client (typically <c>new TelegramBotClient(token)</c>).</param>
    public TelegramBotGateway(ITelegramBotClient client) => _client = client;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TelegramInboundUpdate>> GetUpdatesAsync(int? offset, TimeSpan timeout, CancellationToken ct)
    {
        var timeoutSecs = Math.Max(0, (int)timeout.TotalSeconds);
        var updates = await _client.GetUpdates(
                offset: offset,
                limit: null,
                timeout: timeoutSecs,
                allowedUpdates: AllowedUpdates,
                cancellationToken: ct)
            .ConfigureAwait(false);

        var mapped = new List<TelegramInboundUpdate>(updates.Length);
        foreach (var u in updates)
        {
            var msg = u.Message;
            if (msg is null)
            {
                // We allowed only Message updates, but a stray type is safe to skip.
                continue;
            }

            // Pick the largest available photo size — Telegram orders ascending by area.
            string? photoFileId = null;
            if (msg.Photo is { Length: > 0 } photos)
            {
                photoFileId = photos[^1].FileId;
            }

            var text = !string.IsNullOrEmpty(msg.Text) ? msg.Text
                      : !string.IsNullOrEmpty(msg.Caption) ? msg.Caption!
                      : string.Empty;

            mapped.Add(new TelegramInboundUpdate
            {
                UpdateId = u.Id,
                MessageId = msg.MessageId,
                ChatId = msg.Chat.Id,
                FromUserId = msg.From?.Id,
                FromDisplayName = msg.From?.FirstName ?? msg.From?.Username ?? string.Empty,
                SentAt = msg.Date,
                Text = text,
                PhotoFileId = photoFileId,
                VoiceFileId = msg.Voice?.FileId,
                DocumentFileId = msg.Document?.FileId,
                DocumentFileName = msg.Document?.FileName,
            });
        }
        return mapped;
    }

    /// <inheritdoc/>
    public Task SendTextAsync(long chatId, string text, int? replyToMessageId, CancellationToken ct)
    {
        // Telegram.Bot v22 takes a ReplyParameters struct rather than a bare id — we only ever
        // thread replies against the inbound message, never quote text.
        var reply = replyToMessageId is { } mid ? new ReplyParameters { MessageId = mid } : null;
        return _client.SendMessage(
            chatId: chatId,
            text: text,
            replyParameters: reply,
            cancellationToken: ct);
    }

    /// <inheritdoc/>
    public async Task SendDocumentAsync(long chatId, string path, string? caption, CancellationToken ct)
    {
        // Open as a stream so the SDK can multipart-upload; let it close when done.
        await using var stream = File.OpenRead(path);
        await _client.SendDocument(
                chatId: chatId,
                document: InputFile.FromStream(stream, Path.GetFileName(path)),
                caption: caption,
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DownloadFileAsync(string fileId, string destinationPath, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await using var fs = File.Create(destinationPath);
        _ = await _client.GetInfoAndDownloadFile(fileId, fs, ct).ConfigureAwait(false);
    }
}
