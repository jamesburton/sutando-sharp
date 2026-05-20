using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Sutando.Pipeline.Stages;

/// <summary>
/// Pipeline stage that wraps an <see cref="IChatClient"/>. Buffers a turn's user-side
/// <see cref="TextFrame"/>s, fires a streaming chat request when the turn finalises, and emits
/// each <see cref="ChatResponseUpdate"/> as a <see cref="TextFrame"/>. Honours
/// <see cref="ControlFrame.Interrupt"/> by cancelling any in-flight chat call via a per-turn
/// linked CTS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Conversation history</b>: this stage holds a flat list of <see cref="ChatMessage"/>s
/// across turns. Each finalised user <see cref="TextFrame"/> is appended; each completed
/// assistant response is appended too. Callers that want a custom memory strategy can wrap
/// the underlying <see cref="IChatClient"/> in MEAI's <see cref="ChatClientBuilder"/>
/// middleware (e.g. summarisation, RAG) before passing it in here.
/// </para>
/// <para>
/// <b>Interruption semantics</b>: per the pipeline-wide convention (see <see cref="ControlFrame"/>),
/// when an <see cref="ControlSignal.Interrupt"/> arrives mid-streaming:
/// <list type="number">
///   <item><description>The per-turn <see cref="CancellationTokenSource"/> is cancelled, which aborts the in-flight <c>GetStreamingResponseAsync</c> call.</description></item>
///   <item><description>Any partial assistant message captured so far is appended to the history (so the model "knows" it was cut off).</description></item>
///   <item><description>The interrupt frame is forwarded downstream so TTS / audio-out can flush their own state.</description></item>
/// </list>
/// The pipeline-level <see cref="CancellationToken"/> is NOT cancelled by an interrupt; the
/// stage stays live and accepts the next turn's frames.
/// </para>
/// <para>
/// <b>Turn-complete</b>: after the streaming response completes naturally, the stage emits a
/// <see cref="ControlFrame.TurnComplete"/> so TTS / sinks can mark end-of-turn UI cues.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public sealed class ChatStage : IPipelineStage<PipelineFrame, PipelineFrame>
{
    private readonly IChatClient _client;
    private readonly ChatOptions? _chatOptions;
    private readonly List<ChatMessage> _history = new();
    private readonly string? _systemPrompt;

    /// <summary>Initialise the stage around a MEAI chat client.</summary>
    /// <param name="client">The chat client.</param>
    /// <param name="systemPrompt">Optional system prompt prepended to every conversation.</param>
    /// <param name="chatOptions">Optional MEAI options threaded into every chat call.</param>
    public ChatStage(IChatClient client, string? systemPrompt = null, ChatOptions? chatOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _systemPrompt = systemPrompt;
        _chatOptions = chatOptions;

        if (!string.IsNullOrEmpty(_systemPrompt))
        {
            _history.Add(new ChatMessage(ChatRole.System, _systemPrompt));
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PipelineFrame> ProcessAsync(
        IAsyncEnumerable<PipelineFrame> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        CancellationTokenSource? turnCts = null;
        try
        {
            await foreach (var frame in source.WithCancellation(ct).ConfigureAwait(false))
            {
                switch (frame)
                {
                    case ControlFrame { Signal: ControlSignal.Interrupt }:
                        // Cancel any in-flight per-turn work. The streaming response below
                        // observes this CTS and exits its await foreach promptly.
                        if (turnCts is { } interruptedCts)
                        {
                            try { await interruptedCts.CancelAsync().ConfigureAwait(false); }
                            catch (ObjectDisposedException) { /* already-completed turn; fine. */ }
                        }
                        yield return frame;
                        break;

                    case TextFrame { IsFinal: true } userText:
                        // Forward the user text first so downstream stages see it before the
                        // assistant response starts arriving.
                        yield return frame;

                        _history.Add(new ChatMessage(ChatRole.User, userText.Text));

                        turnCts?.Dispose();
                        turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                        var partial = string.Empty;
                        var observedComplete = false;

                        // Yield each streaming update as a non-final TextFrame; the final
                        // turn-complete control frame fires when the streaming response ends
                        // naturally. We capture the partial response so an interrupt mid-stream
                        // still records what the model said.
                        await foreach (var (text, isComplete) in StreamSafelyAsync(_history, _chatOptions, turnCts.Token).ConfigureAwait(false))
                        {
                            if (!string.IsNullOrEmpty(text))
                            {
                                partial += text;
                                yield return new TextFrame(text, IsFinal: false);
                            }
                            if (isComplete)
                            {
                                observedComplete = true;
                            }
                        }

                        if (!string.IsNullOrEmpty(partial))
                        {
                            _history.Add(new ChatMessage(ChatRole.Assistant, partial));
                        }

                        if (observedComplete)
                        {
                            yield return ControlFrame.TurnComplete;
                        }
                        break;

                    default:
                        // Forward everything we don't transform — VadFrames, AudioInputFrames,
                        // partial TextFrames from upstream, etc. — to downstream consumers.
                        yield return frame;
                        break;
                }
            }
        }
        finally
        {
            turnCts?.Dispose();
        }
    }

    /// <summary>
    /// Run a streaming chat call and yield (text, isComplete) pairs. Catches the
    /// <see cref="OperationCanceledException"/> that fires on interruption so the outer
    /// <c>await foreach</c> can record the partial response cleanly.
    /// </summary>
    private async IAsyncEnumerable<(string Text, bool IsComplete)> StreamSafelyAsync(
        IList<ChatMessage> history,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken turnCt)
    {
        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        try
        {
            enumerator = _client.GetStreamingResponseAsync(history, options, turnCt).GetAsyncEnumerator(turnCt);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }

        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The turn was interrupted. Anything captured so far is already in `partial`.
                    yield break;
                }

                if (!moved)
                {
                    yield return (string.Empty, IsComplete: true);
                    yield break;
                }

                var update = enumerator.Current;
                yield return (update.Text ?? string.Empty, IsComplete: false);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Snapshot of the chat history maintained by this stage. Useful for tests / diagnostics.</summary>
    internal IReadOnlyList<ChatMessage> HistorySnapshot => _history.ToArray();
}
