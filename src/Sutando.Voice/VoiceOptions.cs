namespace Sutando.Voice;

/// <summary>
/// Runtime configuration for the voice WS server. Populated from <c>appsettings</c>, environment
/// variables (<c>SUTANDO_VOICE_PORT</c>, <c>GEMINI_VOICE_API_KEY</c>, <c>GEMINI_API_KEY</c>) and
/// command-line args (<c>--port</c>). The host's resolver applies the precedence
/// <b>CLI args &gt; environment &gt; configuration</b>.
/// </summary>
public sealed class VoiceOptions
{
    /// <summary>The <c>IConfiguration</c> binding key. Maps to a top-level <c>Voice:*</c> section.</summary>
    public const string SectionName = "Voice";

    /// <summary>TCP port the WS server listens on. Default <c>9900</c> mirrors upstream sutando.</summary>
    public int Port { get; set; } = 9900;

    /// <summary>
    /// Gemini API key. <see cref="VoiceServer"/> reads <c>GEMINI_VOICE_API_KEY</c> first, then falls
    /// back to <c>GEMINI_API_KEY</c>; either value lands here. Empty when no key is set — the WS
    /// handler then refuses the upgrade with HTTP 503 instead of attempting a doomed Gemini connect.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The Gemini model id used for each session. Mirrors upstream's default voice model.</summary>
    public string Model { get; set; } = "gemini-2.5-flash-live-preview";

    /// <summary>Prebuilt voice id. <c>Puck</c> matches upstream's default.</summary>
    public string VoiceName { get; set; } = "Puck";

    /// <summary>Optional system prompt for every session. Null disables.</summary>
    public string? SystemInstruction { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the voice server runs each connection through the in-process
    /// local-inference pipeline (STT → Chat → TTS) instead of Gemini Live — the
    /// <c>sutando voice --local</c> mode. Set via the <c>--local</c> CLI flag, the
    /// <c>SUTANDO_VOICE_LOCAL</c> env var, or <c>Voice:UseLocal</c> in configuration.
    /// </summary>
    /// <remarks>
    /// In local mode the Gemini-specific options (<see cref="ApiKey"/>, <see cref="Model"/>,
    /// <see cref="VoiceName"/>) are unused; the model files come from <see cref="LocalModels"/>.
    /// </remarks>
    public bool UseLocal { get; set; }

    /// <summary>
    /// Filesystem paths to the local-inference model files used when <see cref="UseLocal"/> is
    /// set. Populated from <c>SUTANDO_*_MODEL</c> environment variables; empty when unset, which
    /// makes the WS upgrade fail gracefully with a clear error envelope.
    /// </summary>
    public LocalModelPaths LocalModels { get; set; } = new();
}

/// <summary>
/// Filesystem locations of the four local-inference model files the <c>--local</c> voice
/// pipeline needs. Resolved from environment variables at server-build time.
/// </summary>
/// <remarks>
/// The local-inference adapters do not download models — operators ship them alongside the
/// deployment. When a path is empty or the file is missing, the <c>--local</c> voice transport
/// rejects the WebSocket upgrade with an explanatory error rather than crashing the host.
/// </remarks>
public sealed class LocalModelPaths
{
    /// <summary>Path to the Whisper GGML model (<c>SUTANDO_WHISPER_MODEL</c>), e.g. <c>ggml-base.en.bin</c>.</summary>
    public string? WhisperModel { get; set; }

    /// <summary>Path to the LlamaSharp GGUF chat model (<c>SUTANDO_LLAMA_MODEL</c>), e.g. <c>Qwen3-8B-Q4_K_M.gguf</c>.</summary>
    public string? LlamaModel { get; set; }

    /// <summary>Path to the KokoroSharp ONNX TTS model (<c>SUTANDO_KOKORO_MODEL</c>).</summary>
    public string? KokoroModel { get; set; }

    /// <summary>
    /// Path to the Silero VAD ONNX model (<c>SUTANDO_SILERO_MODEL</c>). When empty, the
    /// auto-download Silero registration is used instead — the model is ~2 MB and cached locally.
    /// </summary>
    public string? SileroModel { get; set; }
}
