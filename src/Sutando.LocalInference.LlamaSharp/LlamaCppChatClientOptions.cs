using System.Diagnostics.CodeAnalysis;
using LLama.Abstractions;
using LLama.Common;

namespace Sutando.LocalInference.LlamaSharp;

/// <summary>
/// Construction options for <see cref="LlamaCppChatClient"/>. Pinned to a single GGUF model
/// file with optional context-window / generation-config knobs and an optional system prompt
/// that is prepended to every conversation.
/// </summary>
/// <remarks>
/// Keeps the constructor surface narrow: the model path is required, everything else has a
/// reasonable default suitable for an 8-billion-parameter chat model on a developer laptop
/// (Qwen3-8B-GGUF Q4_K_M is the recommended local pick — see
/// <c>docs/local-stack-scope.md</c>). Mutable getters/setters by design — the DI extensions
/// expose a <c>configure</c> callback that wants to mutate this in place.
/// </remarks>
[Experimental("SUTANDO001")]
public sealed class LlamaCppChatClientOptions
{
    /// <summary>Default context-window size (tokens) when not overridden.</summary>
    public const uint DefaultContextSize = 4096;

    /// <summary>Default number of GPU layers — 0 = pure CPU, the CI-portable default.</summary>
    public const int DefaultGpuLayerCount = 0;

    /// <summary>
    /// Initialize an options bag pinned to the given GGUF model file. Other knobs default to
    /// values suitable for a developer-laptop chat session.
    /// </summary>
    /// <param name="modelPath">Path to the GGUF model file on disk. Required.</param>
    /// <exception cref="ArgumentException"><paramref name="modelPath"/> is null / whitespace.</exception>
    public LlamaCppChatClientOptions(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ModelPath = modelPath;
    }

    /// <summary>Path to the GGUF model file on disk.</summary>
    public string ModelPath { get; }

    /// <summary>Optional system prompt prepended to every conversation as the first message.</summary>
    /// <remarks>If both this and a <c>system</c>-role message are passed at call time, both are sent (this one first).</remarks>
    public string? SystemPrompt { get; set; }

    /// <summary>Context-window size in tokens.</summary>
    public uint ContextSize { get; set; } = DefaultContextSize;

    /// <summary>Number of model layers to offload to GPU. 0 = CPU-only.</summary>
    public int GpuLayerCount { get; set; } = DefaultGpuLayerCount;

    /// <summary>
    /// Optional callback to mutate the <see cref="ModelParams"/> just before
    /// <see cref="LLama.LLamaWeights.LoadFromFile(IModelParams)"/> is invoked. Use for advanced
    /// settings (RoPE freqs, embedding mode, BatchSize) that we don't surface directly.
    /// </summary>
    public Action<ModelParams>? ConfigureModelParams { get; set; }

    /// <summary>
    /// Optional factory for the <see cref="ILLamaExecutor"/>. Defaults to creating an
    /// <see cref="LLama.InteractiveExecutor"/> over an <see cref="LLama.LLamaContext"/>, which is the
    /// right shape for chat — the stateless executor is better suited to one-shot completions.
    /// </summary>
    public Func<LLama.LLamaContext, ILLamaExecutor>? ExecutorFactory { get; set; }
}
