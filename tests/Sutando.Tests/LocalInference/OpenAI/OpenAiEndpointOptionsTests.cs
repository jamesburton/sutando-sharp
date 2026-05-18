using Sutando.LocalInference.OpenAI;

namespace Sutando.Tests.LocalInference.OpenAI;

public sealed class OpenAiEndpointOptionsTests
{
    [Fact]
    public void ResolvedApiKey_DefaultsToSentinel_WhenApiKeyIsNullOrEmpty()
    {
        var opts = new OpenAiEndpointOptions { Endpoint = new Uri("http://localhost:8000/v1") };
        Assert.Equal(OpenAiEndpointOptions.LocalSentinelApiKey, opts.ResolvedApiKey);

        var withEmpty = opts with { ApiKey = string.Empty };
        Assert.Equal(OpenAiEndpointOptions.LocalSentinelApiKey, withEmpty.ResolvedApiKey);
    }

    [Fact]
    public void ResolvedApiKey_PassesThroughWhenSupplied()
    {
        var opts = new OpenAiEndpointOptions
        {
            Endpoint = new Uri("https://api.together.xyz/v1"),
            ApiKey = "sk-real-key",
        };
        Assert.Equal("sk-real-key", opts.ResolvedApiKey);
    }

    [Fact]
    public void Record_ImmutableUpdate_LeavesOriginalUntouched()
    {
        var opts = new OpenAiEndpointOptions { Endpoint = new Uri("http://localhost:8000/v1"), Model = "Qwen3-8B-AWQ" };
        var tweaked = opts with { Model = "Llama-3-70B", ApiKey = "k" };

        Assert.Equal("Qwen3-8B-AWQ", opts.Model);
        Assert.Null(opts.ApiKey);
        Assert.Equal("Llama-3-70B", tweaked.Model);
        Assert.Equal("k", tweaked.ApiKey);
    }
}
