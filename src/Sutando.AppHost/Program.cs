// Sutando.AppHost — .NET Aspire 13 orchestrator for the local-inference fleet.
//
// Wires four resources into a DistributedApplication:
//   - llm   — vLLM container serving Qwen3-8B-AWQ on :8000.
//   - stt   — speaches container serving an OpenAI-compatible STT API on :8000.
//   - tts   — kokoro-fastapi container serving an OpenAI-compatible TTS API on :8880.
//   - agent — the Sutando.Cli .NET project, with WithReference() pulls on the
//             three Python services so MEAI service discovery (services__<name>__http__0)
//             resolves the endpoint URIs automatically.
//
// All three Python services are defined via per-service Dockerfiles in
// `dockerfiles/`. The Dockerfiles are deliberately thin (FROM upstream image,
// then ENV defaults + EXPOSE), which gives us a documented customisation seam —
// add proxy/cert handling, multi-stage build, etc. — without changing this file.
//
// Model + voice + runtime-mode knobs are exposed via Aspire parameters that
// roll through to the container's environment variables. Defaults are pinned to
// the values recommended in docs/local-stack-scope.md §6.
//
// The compose-out path (`aspire publish` against the AddDockerComposeEnvironment
// resource) generates `aspire-output/docker-compose.yml` for operators who don't
// want Aspire on their host. See INTEGRATION-NOTES.md for the exact invocation.
//
// ---------------------------------------------------------------------------
// Why conventional Main + namespace instead of top-level statements
//
// Aspire's stock template ships top-level statements which compile to an
// internal Program class in the GLOBAL namespace. The Sutando solution already
// has several other top-level entry-points (Sutando.Api, Sutando.Dashboard,
// Sutando.Voice, Sutando.Phone) that each compile to their own global Program
// — and the existing Api / Dashboard tests reach those via
// `WebApplicationFactory<Program>` with `using Sutando.Api;` etc. to
// disambiguate.
//
// When tests/Sutando.Tests.csproj references Sutando.AppHost (so the AppHost
// tests can reach its types via reflection), WebApplicationFactory's host
// detection falls over: it sees the AppHost's global Program first and tries
// to invoke `DistributedApplication.Run()` against it, which fails on
// DCP-validation (no DCP installed in the test environment, by design).
//
// Wrapping our entry-point in a namespaced class fully sidesteps that
// collision — there is no global Program coming out of this csproj, and the
// other projects' Program classes stay reachable through their existing
// namespace-qualified lookups.
// ---------------------------------------------------------------------------

using Aspire.Hosting;

namespace Sutando.AppHost;

/// <summary>
/// Sutando.AppHost orchestrator entry-point. Hosts the four-resource
/// DistributedApplication that fronts the local-inference fleet, and serves
/// as the type-marker for Aspire.Hosting.Testing's
/// <c>DistributedApplicationTestingBuilder.CreateAsync&lt;Program&gt;()</c>.
/// </summary>
public sealed class Program
{
    // Hide the parameterless constructor — Program is a static entry-point shim,
    // not something callers should instantiate.
    private Program() { }

    /// <summary>
    /// Build and run the Aspire <c>DistributedApplication</c>. Called by the
    /// .NET runtime when the AppHost is invoked as an executable (e.g.
    /// <c>dotnet run --project src/Sutando.AppHost</c>) and by
    /// <c>Aspire.Hosting.Testing.DistributedApplicationTestingBuilder</c> via
    /// reflection.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the builder.</param>
    public static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Docker-compose deployment target. The presence of this resource is what
        // allows `aspire publish` to materialise a `docker-compose.yml` (plus
        // per-service build contexts) under the configured --output-path.
        builder.AddDockerComposeEnvironment("local-stack");

        // -------------------------------------------------------------------
        // Configurable knobs — model paths, voice IDs, GPU/CPU mode.
        //
        // AddParameter with publishValueAsDefault=true bakes the default into
        // the generated manifest / compose file; operators override at deploy
        // time by setting the corresponding environment variable or
        // user-secret. The names match the env-var convention documented in
        // INTEGRATION-NOTES.md so a `LLM_MODEL=...` shell export works for
        // both `aspire run` and the generated `docker-compose up`.
        // -------------------------------------------------------------------

        // Qwen/Qwen3-8B-AWQ is the recon-recommended LLM weight quant
        // (Apache 2.0, official Qwen release, ~5.5 GB, runs on a single
        // 12-16 GB GPU under vLLM). Override with a smaller variant on
        // resource-constrained dev boxes.
        var llmModel = builder.AddParameter(
            "LlmModel",
            value: "Qwen/Qwen3-8B-AWQ",
            publishValueAsDefault: true);

        // faster-whisper medium.en is the speaches default per scope doc §3.
        // Swap to faster-whisper-large-v3 or a multilingual variant via this
        // knob.
        var sttModel = builder.AddParameter(
            "SttModel",
            value: "Systran/faster-whisper-medium.en",
            publishValueAsDefault: true);

        // Kokoro stock voice — af_bella is the upstream-recommended default.
        var ttsVoice = builder.AddParameter(
            "TtsVoice",
            value: "af_bella",
            publishValueAsDefault: true);

        // GPU vs CPU runtime toggle. The Dockerfiles read $RUNTIME_DEVICE to
        // flip between the GPU-enabled base image and the CPU-only variant
        // where the upstream publisher ships both (vLLM and kokoro-fastapi
        // do; speaches detects CUDA automatically at runtime). Default is
        // "gpu" so a workstation works out of the box; laptops should
        // `export RUNTIME_DEVICE=cpu` before `aspire run` / compose-up.
        var runtimeDevice = builder.AddParameter(
            "RuntimeDevice",
            value: "gpu",
            publishValueAsDefault: true);

        // -------------------------------------------------------------------
        // LLM service — vLLM container, OpenAI-compatible
        // /v1/chat/completions.
        //
        // AddDockerfile (vs AddContainer) lets operators patch the Dockerfile
        // (proxy, CA certs, alternate base image) without touching code. The
        // vLLM image listens on :8000 by default; we surface it as the
        // canonical MEAI service-discovery name "llm" so the agent's
        // IConfiguration reads `services__llm__http__0` automatically when
        // the AppHost wires the reference.
        // -------------------------------------------------------------------
        var llm = builder.AddDockerfile(
                "llm",
                contextPath: "dockerfiles/llm",
                dockerfilePath: "Dockerfile")
            .WithHttpEndpoint(targetPort: 8000, name: "http")
            .WithEnvironment("LLM_MODEL", llmModel)
            .WithEnvironment("RUNTIME_DEVICE", runtimeDevice);

        // -------------------------------------------------------------------
        // STT service — speaches (formerly faster-whisper-server) container.
        // OpenAI-compatible /v1/audio/transcriptions endpoint on :8000.
        // -------------------------------------------------------------------
        var stt = builder.AddDockerfile(
                "stt",
                contextPath: "dockerfiles/stt",
                dockerfilePath: "Dockerfile")
            .WithHttpEndpoint(targetPort: 8000, name: "http")
            .WithEnvironment("WHISPER__MODEL", sttModel)
            .WithEnvironment("RUNTIME_DEVICE", runtimeDevice);

        // -------------------------------------------------------------------
        // TTS service — kokoro-fastapi container.
        // OpenAI-compatible /v1/audio/speech endpoint. Upstream image listens
        // on :8880.
        // -------------------------------------------------------------------
        var tts = builder.AddDockerfile(
                "tts",
                contextPath: "dockerfiles/tts",
                dockerfilePath: "Dockerfile")
            .WithHttpEndpoint(targetPort: 8880, name: "http")
            .WithEnvironment("KOKORO_DEFAULT_VOICE", ttsVoice)
            .WithEnvironment("RUNTIME_DEVICE", runtimeDevice);

        // -------------------------------------------------------------------
        // Sutando agent — the .NET CLI project, wired with service-discovery
        // references to all three Python services.
        //
        // WithReference(EndpointReference) emits environment variables of the
        // form `services__<name>__http__0=<uri>` that MEAI's
        // IConfiguration-backed service-discovery layer resolves automatically
        // when the agent constructs IChatClient / ISpeechToTextClient /
        // ITextToSpeechClient instances.
        //
        // Container resources don't implement `IResourceWithServiceDiscovery`
        // directly (that interface is reserved for project resources), so we
        // wire references at the endpoint level. Passing
        // `llm.GetEndpoint("http")` produces the same service-discovery
        // environment variable shape the agent already expects.
        //
        // The agent reads the URLs via:
        //   builder.Configuration["services:llm:http:0"]
        //   builder.Configuration["services:stt:http:0"]
        //   builder.Configuration["services:tts:http:0"]
        //
        // Sutando.LocalInference.OpenAI (task #17, in flight) will own the
        // HttpClient → IChatClient conversion. Until that ships, the agent
        // can construct raw HttpClient instances against these URIs — see
        // INTEGRATION-NOTES.md.
        // -------------------------------------------------------------------
        builder.AddProject<Projects.Sutando_Cli>("agent")
            .WithReference(llm.GetEndpoint("http"))
            .WithReference(stt.GetEndpoint("http"))
            .WithReference(tts.GetEndpoint("http"))
            .WaitFor(llm)
            .WaitFor(stt)
            .WaitFor(tts);

        builder.Build().Run();
    }
}
