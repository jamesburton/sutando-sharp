using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using Microsoft.Extensions.AI;

namespace Sutando.LocalInference.LlamaSharp;

/// <summary>
/// Microsoft.Extensions.AI <see cref="IChatClient"/> backed by LlamaSharp's bindings to
/// llama.cpp. Loads a GGUF model file once, owns the weights / context / executor lifetime,
/// and delegates the request shape to LlamaSharp's built-in
/// <see cref="LLamaExecutorExtensions.AsChatClient(ILLamaExecutor, IHistoryTransform?, ITextStreamTransform?)"/>
/// adapter under the hood.
/// </summary>
/// <remarks>
/// <para>
/// We intentionally do NOT reimplement the
/// <see cref="ChatMessage"/> → llama-prompt mapping; LlamaSharp's own adapter handles it
/// (including the System / User / Assistant role mapping and stateful-vs-stateless executor
/// branching). Our job is the DI shape + resource ownership.
/// </para>
/// <para>
/// <b>Tool / function calling</b>: LlamaSharp 0.27's
/// <c>AsChatClient</c> wrapper does not yet flow <see cref="ChatOptions.Tools"/> into the model
/// prompt. Local Qwen3 / Llama / Mistral models can technically be coaxed into function-calling
/// via prompt-engineering, but the round-trip is brittle without a model-specific template.
/// Tools are therefore <b>accepted but ignored</b> — the caller's tools are passed through
/// transparently in case a future LlamaSharp release adds support. Document this caveat to your
/// consumers; for reliable function-calling against a local model, layer
/// <see cref="ChatClientBuilderChatClientExtensions.UseFunctionInvocation(ChatClientBuilder, Microsoft.Extensions.Logging.ILoggerFactory?, Action{FunctionInvokingChatClient}?)"/>
/// over this client and rely on prompt-based JSON-mode prompting instead.
/// </para>
/// <para>
/// <b>System prompts</b>: if <see cref="LlamaCppChatClientOptions.SystemPrompt"/> is set, it's
/// prepended (once, as the first system message) to every conversation passed to
/// <see cref="GetResponseAsync"/> / <see cref="GetStreamingResponseAsync"/>. Additional
/// <see cref="ChatRole.System"/> messages from the caller are retained in order.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public sealed class LlamaCppChatClient : IChatClient
{
    private readonly LlamaCppChatClientOptions _options;
    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly ILLamaExecutor _executor;
    private readonly IChatClient _inner;
    private bool _disposed;

    /// <summary>
    /// Load the GGUF model at <see cref="LlamaCppChatClientOptions.ModelPath"/> and prepare the
    /// chat client for synchronous use.
    /// </summary>
    /// <param name="options">Construction options carrying model path + tuning knobs.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="LlamaCppChatClientOptions.ModelPath"/> is null / empty.</exception>
    public LlamaCppChatClient(LlamaCppChatClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelPath);

        _options = options;

        var modelParams = new ModelParams(options.ModelPath)
        {
            ContextSize = options.ContextSize,
            GpuLayerCount = options.GpuLayerCount,
        };
        options.ConfigureModelParams?.Invoke(modelParams);

        _weights = LLamaWeights.LoadFromFile(modelParams);
        _context = _weights.CreateContext(modelParams);
        _executor = options.ExecutorFactory is { } factory
            ? factory(_context)
            : new InteractiveExecutor(_context);

        _inner = _executor.AsChatClient();
    }

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _inner.GetResponseAsync(PrependSystemPrompt(messages), options, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return StreamCoreAsync(PrependSystemPrompt(messages), options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamCoreAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        // Prefer the inner client's answers (it surfaces ChatClientMetadata + the executor itself
        // when asked); fall back to handing back this wrapper if the caller asks for it.
        return _inner.GetService(serviceType) ?? (serviceType.IsInstanceOfType(this) ? this : null);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Dispose order matters: the inner client first (so it stops touching the executor),
        // then context, then weights. Weights are the heaviest allocation (the model itself).
        _inner.Dispose();
        _context.Dispose();
        _weights.Dispose();
    }

    /// <summary>
    /// Prepend the configured <see cref="LlamaCppChatClientOptions.SystemPrompt"/> (if any) as
    /// the first system message in the sequence. The caller's messages follow in order.
    /// </summary>
    internal IEnumerable<ChatMessage> PrependSystemPrompt(IEnumerable<ChatMessage> messages)
    {
        if (!string.IsNullOrEmpty(_options.SystemPrompt))
        {
            yield return new ChatMessage(ChatRole.System, _options.SystemPrompt);
        }

        foreach (var message in messages)
        {
            yield return message;
        }
    }
}
