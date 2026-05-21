using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Sutando.LocalInference;
using Sutando.Voice.Local;

namespace Sutando.Tests.Voice.Local;

/// <summary>
/// In-process fakes for the four local-inference stage components, plus a helper that assembles
/// them into a <see cref="LocalPipelineOptions"/>. Mirrors the scripted-client pattern the
/// <c>Sutando.Tests/Pipeline/Stages</c> tests already use — no real model downloads required.
/// </summary>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
[SuppressMessage("Usage", "MEAI001", Justification = "Tests against experimental MEAI surfaces.")]
internal static class LocalPipelineFakes
{
    /// <summary>
    /// Build a <see cref="LocalPipelineOptions"/> wired to fakes: a VAD that brackets the first
    /// audio chunk with a speech turn, an STT that returns <paramref name="transcript"/>, a chat
    /// client that streams <paramref name="chatChunks"/>, and a TTS that emits one PCM frame per
    /// synthesised sentence.
    /// </summary>
    public static LocalPipelineOptions Build(string transcript, params string[] chatChunks) => new()
    {
        VadDetector = new BracketingVadDetector(),
        SpeechToText = new ScriptedSttClient(transcript),
        Chat = new ScriptedChatClient(chatChunks.Length > 0 ? chatChunks : new[] { "ok." }),
        TextToSpeech = new EchoTtsClient(),
    };
}

/// <summary>
/// VAD fake bracketing an audio burst into exactly one speech turn.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the timing matters.</b> <c>VadStage</c> feeds audio into the detector on a background
/// channel and only drains the detector's events (via <c>TryRead</c>) when the <i>next</i> audio
/// frame arrives. So a <c>SpeechStart</c> must be available before the audio frames that should
/// land inside the turn, or the STT stage buffers nothing.
/// </para>
/// <para>
/// This fake therefore emits <see cref="VadEventKind.SpeechStart"/> <b>before</b> consuming any
/// audio (it is queued the instant the detector task starts), then emits
/// <see cref="VadEventKind.SpeechEnd"/> after the third audio frame — so a test that sends ≥ 4
/// chunks gets a deterministic turn with audio buffered between the edges.
/// </para>
/// </remarks>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
internal sealed class BracketingVadDetector : IVadDetector
{
    /// <summary>The audio-frame index after which <see cref="VadEventKind.SpeechEnd"/> is emitted.</summary>
    public const int SpeechEndAfterFrame = 3;

    /// <inheritdoc/>
    public string Id => "fake-bracketing";

    /// <inheritdoc/>
    public async IAsyncEnumerable<VadEvent> AnalyzeAsync(
        IAsyncEnumerable<AudioFrame> source,
        VadOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Emitted before any audio is consumed — guaranteed available to the STT stage before the
        // audio frames that should land inside the turn.
        yield return VadEvent.SpeechStart(DateTimeOffset.UtcNow, 0.95f);

        var index = 0;
        var endEmitted = false;
        await foreach (var _ in source.WithCancellation(ct).ConfigureAwait(false))
        {
            index++;
            if (!endEmitted && index >= SpeechEndAfterFrame)
            {
                endEmitted = true;
                yield return VadEvent.SpeechEnd(DateTimeOffset.UtcNow, 0.05f);
            }
        }

        // If the stream ended before SpeechEndAfterFrame frames arrived, still close the turn so a
        // short burst is transcribed rather than dropped.
        if (!endEmitted)
        {
            yield return VadEvent.SpeechEnd(DateTimeOffset.UtcNow, 0.05f);
        }
    }
}

/// <summary>STT fake that returns a fixed transcript for every turn.</summary>
[SuppressMessage("Usage", "MEAI001", Justification = "Tests against experimental MEAI surfaces.")]
internal sealed class ScriptedSttClient : ISpeechToTextClient
{
    private readonly string _transcript;

    public ScriptedSttClient(string transcript) => _transcript = transcript;

    public Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new SpeechToTextResponse(_transcript));

    public IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
        => Empty();

    private static async IAsyncEnumerable<SpeechToTextResponseUpdate> Empty()
    {
        await Task.Yield();
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Chat fake that streams a fixed sequence of assistant chunks.</summary>
[SuppressMessage("Usage", "MEAI001", Justification = "Tests against experimental MEAI surfaces.")]
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly string[] _chunks;

    public ScriptedChatClient(string[] chunks) => _chunks = chunks;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse());

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in _chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>TTS fake that emits one PCM frame per synthesised sentence.</summary>
[SuppressMessage("Usage", "MEAI001", Justification = "Tests against experimental MEAI surfaces.")]
internal sealed class EchoTtsClient : ITextToSpeechClient
{
    public Task<TextToSpeechResponse> GetAudioAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new TextToSpeechResponse());

    public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(
        string text,
        TextToSpeechOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        // Distinct payload length per call so a test can tell two synthesised sentences apart.
        var pcm = new byte[Math.Max(text.Length * 2, 4)];
        yield return new TextToSpeechResponseUpdate(
        [
            new DataContent(pcm, "audio/pcm; rate=24000; channels=1; bits=16"),
        ]);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
