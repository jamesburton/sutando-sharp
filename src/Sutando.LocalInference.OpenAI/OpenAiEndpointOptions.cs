using System.Diagnostics.CodeAnalysis;

namespace Sutando.LocalInference.OpenAI;

/// <summary>
/// Configuration shared by every OpenAI-compatible adapter. The chat / STT / TTS
/// services point at the same shape (a base URL, an optional API key, an optional
/// model identifier) so callers can DI-register one record and reuse it across stages.
/// </summary>
/// <remarks>
/// <para>
/// Local self-hosted endpoints (vLLM, llama-server, speaches, kokoro-fastapi, LM Studio)
/// generally don't require an API key but accept one for compatibility. Cloud-hosted
/// compatibles (TogetherAI, Groq, Anyscale, Fireworks, OpenAI-proper) require one.
/// </para>
/// <para>
/// When <see cref="ApiKey"/> is <see langword="null"/> we send a sentinel value so the
/// underlying SDK still constructs a valid request; the local stack ignores it.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public sealed record OpenAiEndpointOptions
{
    /// <summary>
    /// Sentinel sent to local endpoints that don't validate the bearer token.
    /// Matches the convention used by upstream local-LLM tooling (LM Studio CLI,
    /// llama-server's <c>--api-key</c> default).
    /// </summary>
    public const string LocalSentinelApiKey = "no-key";

    /// <summary>
    /// Base endpoint URL — e.g. <c>http://localhost:8000/v1</c> for vLLM,
    /// <c>http://stt:8000/v1</c> when running under Aspire service-discovery,
    /// <c>https://api.openai.com/v1</c> for the OpenAI API itself.
    /// </summary>
    public required Uri Endpoint { get; init; }

    /// <summary>
    /// API key. When <see langword="null"/>, <see cref="LocalSentinelApiKey"/> is used —
    /// local services accept any non-empty key.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Model identifier to send in each request. Defaults vary per stage:
    /// chat = <c>Qwen3-8B-AWQ</c>, STT = <c>Systran/faster-whisper-medium.en</c>,
    /// TTS = <c>kokoro</c>. Adapters fall back to a stage-specific default when this is
    /// <see langword="null"/>.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Resolve the effective API key (sentinel when caller didn't supply one).
    /// </summary>
    public string ResolvedApiKey => string.IsNullOrEmpty(ApiKey) ? LocalSentinelApiKey : ApiKey;
}
