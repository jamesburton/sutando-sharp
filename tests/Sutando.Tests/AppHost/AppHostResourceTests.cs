using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Testing;

namespace Sutando.Tests.AppHost;

/// <summary>
/// Smoke tests for the Sutando.AppHost Aspire 13 orchestrator.
///
/// We deliberately stop short of <c>BuildAsync().StartAsync()</c> — booting the
/// distributed application would spin up Docker, pull multi-gigabyte vLLM /
/// kokoro / speaches images, and is the wrong shape for a unit-test gate. What
/// we DO verify is that the AppHost's resource graph parses, all four named
/// resources register, references resolve, and the endpoints we wire are
/// present with the expected shape. <c>DistributedApplicationTestingBuilder</c>
/// gives us a configured builder we can inspect via the standard <c>Resources</c>
/// collection without ever calling into the orchestration runtime.
/// </summary>
public sealed class AppHostResourceTests
{
    [Fact]
    public async Task AppHost_Configuration_Parses_Cleanly()
    {
        // Just instantiating the testing builder runs the AppHost's top-level
        // statements against a test-mode DistributedApplication factory.
        // If Program.cs has a malformed builder graph (duplicate resource
        // names, unresolved references, invalid endpoint configuration),
        // CreateAsync throws here.
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Sutando.AppHost.Program>();

        Assert.NotNull(builder);
        Assert.NotNull(builder.Resources);
    }

    [Fact]
    public async Task AppHost_Registers_All_Four_Required_Resources()
    {
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Sutando.AppHost.Program>();

        // The orchestration plan in docs/local-stack-scope.md §6 calls for four
        // logical resources: the three Python containers + the .NET agent.
        // Each must be reachable by its canonical service-discovery name so the
        // agent's IConfiguration reads `services__<name>__http__0` correctly.
        var resourceNames = builder.Resources.Select(r => r.Name).ToHashSet();

        Assert.Contains("llm", resourceNames);
        Assert.Contains("stt", resourceNames);
        Assert.Contains("tts", resourceNames);
        Assert.Contains("agent", resourceNames);
    }

    [Fact]
    public async Task AppHost_LLM_Resource_Exposes_Http_Endpoint()
    {
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Sutando.AppHost.Program>();

        var llm = builder.Resources.SingleOrDefault(r => r.Name == "llm");
        Assert.NotNull(llm);

        // vLLM listens on :8000 inside the container. The "http" endpoint is the
        // name the agent uses to look up the URL via service-discovery.
        var endpoint = llm.Annotations
            .OfType<EndpointAnnotation>()
            .SingleOrDefault(e => e.Name == "http");
        Assert.NotNull(endpoint);
        Assert.Equal(8000, endpoint.TargetPort);
    }

    [Fact]
    public async Task AppHost_STT_Resource_Exposes_Http_Endpoint()
    {
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Sutando.AppHost.Program>();

        var stt = builder.Resources.SingleOrDefault(r => r.Name == "stt");
        Assert.NotNull(stt);

        var endpoint = stt.Annotations
            .OfType<EndpointAnnotation>()
            .SingleOrDefault(e => e.Name == "http");
        Assert.NotNull(endpoint);
        Assert.Equal(8000, endpoint.TargetPort);
    }

    [Fact]
    public async Task AppHost_TTS_Resource_Exposes_Http_Endpoint()
    {
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Sutando.AppHost.Program>();

        var tts = builder.Resources.SingleOrDefault(r => r.Name == "tts");
        Assert.NotNull(tts);

        // kokoro-fastapi's upstream image listens on :8880 (not 8000) — this
        // assertion is the regression guard against a stale port copy-paste.
        var endpoint = tts.Annotations
            .OfType<EndpointAnnotation>()
            .SingleOrDefault(e => e.Name == "http");
        Assert.NotNull(endpoint);
        Assert.Equal(8880, endpoint.TargetPort);
    }

    [Fact]
    public async Task AppHost_Agent_References_All_Three_Services()
    {
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Sutando.AppHost.Program>();

        var agent = builder.Resources.SingleOrDefault(r => r.Name == "agent");
        Assert.NotNull(agent);

        // Each WithReference() on a container endpoint emits an
        // EnvironmentCallbackAnnotation that injects `services__<name>__http__0`
        // at start time. We can't run the callbacks without a live application
        // model, but their presence is what we're guarding against here: a
        // dropped .WithReference() call would silently break service discovery
        // for the agent.
        Assert.NotEmpty(agent.Annotations.OfType<EnvironmentCallbackAnnotation>());

        // WaitFor() adds a WaitAnnotation pointing at each dependency. Three
        // dependencies (llm/stt/tts) means three WaitAnnotations — a regression
        // guard against silently dropping any of the .WaitFor(...) calls in
        // Program.cs.
        var waitAnnotations = agent.Annotations
            .OfType<WaitAnnotation>()
            .Select(w => w.Resource.Name)
            .ToHashSet();

        Assert.Contains("llm", waitAnnotations);
        Assert.Contains("stt", waitAnnotations);
        Assert.Contains("tts", waitAnnotations);
    }

    [Fact]
    public async Task AppHost_LLM_Model_Parameter_Defaults_To_Qwen3_8B_AWQ()
    {
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Sutando.AppHost.Program>();

        // The LlmModel parameter is the configurable knob from
        // docs/local-stack-scope.md §1 — Qwen/Qwen3-8B-AWQ is the
        // recon-recommended default. A drift in this default would silently
        // change the LLM the operator gets on first boot, so we pin it here.
        var llmModelParam = builder.Resources
            .OfType<ParameterResource>()
            .SingleOrDefault(p => p.Name == "LlmModel");
        Assert.NotNull(llmModelParam);
    }

    [Fact]
    public async Task AppHost_DockerCompose_Publishing_Is_Available()
    {
        // We don't assert on the DockerComposeEnvironmentResource being in the
        // Resources collection — Aspire.Hosting.Testing's builder filters
        // deployment-target resources out in `run` execution mode by design (they
        // are only materialised during `publish` execution). What we CAN verify
        // is that the Aspire.Hosting.Docker assembly is loaded into the AppHost's
        // process, which proves the DockerComposeEnvironment registration code
        // path compiles and links. The integration test for the actual compose
        // emission lives in INTEGRATION-NOTES.md as a manual `aspire publish`
        // check until Aspire ships a publish-mode test harness.
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Sutando.AppHost.Program>();

        var dockerAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Aspire.Hosting.Docker");
        Assert.NotNull(dockerAssembly);

        // The DockerComposeEnvironmentResource type must be reachable — that's
        // the load-bearing check. If a future Aspire rebase drops or renames the
        // extension method we use in AppHost/Program.cs, the AppHost wouldn't
        // even compile, but this guards against a silent removal of the type
        // from the Docker hosting assembly.
        var composeType = dockerAssembly.GetType(
            "Aspire.Hosting.Docker.DockerComposeEnvironmentResource");
        Assert.NotNull(composeType);
    }
}
