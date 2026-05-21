using Microsoft.Extensions.Configuration;
using Sutando.Voice;

namespace Sutando.Tests.Voice.Local;

/// <summary>
/// Unit tests for <see cref="VoiceServer.ResolveUseLocal"/> — the CLI / env / config precedence
/// that decides whether the voice server boots in local-inference mode.
/// </summary>
public sealed class VoiceServerLocalResolutionTests
{
    private static IConfiguration EmptyConfig { get; } =
        new ConfigurationBuilder().Build();

    [Fact]
    public void Defaults_to_false_when_nothing_is_set()
    {
        WithoutLocalEnv(() => Assert.False(VoiceServer.ResolveUseLocal(EmptyConfig, [])));
    }

    [Fact]
    public void Bare_local_flag_enables_local_mode()
    {
        WithoutLocalEnv(() => Assert.True(VoiceServer.ResolveUseLocal(EmptyConfig, ["--local"])));
    }

    [Theory]
    [InlineData("--local=true", true)]
    [InlineData("--local=false", false)]
    public void Local_flag_with_explicit_value_is_honoured(string arg, bool expected)
    {
        WithoutLocalEnv(() => Assert.Equal(expected, VoiceServer.ResolveUseLocal(EmptyConfig, [arg])));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("on")]
    public void Env_var_truthy_values_enable_local_mode(string value)
    {
        WithLocalEnv(value, () => Assert.True(VoiceServer.ResolveUseLocal(EmptyConfig, [])));
    }

    [Fact]
    public void Config_key_enables_local_mode_when_cli_and_env_are_absent()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{VoiceOptions.SectionName}:UseLocal"] = "true",
            })
            .Build();

        WithoutLocalEnv(() => Assert.True(VoiceServer.ResolveUseLocal(config, [])));
    }

    [Fact]
    public void Cli_flag_overrides_a_false_config_key()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{VoiceOptions.SectionName}:UseLocal"] = "false",
            })
            .Build();

        WithoutLocalEnv(() => Assert.True(VoiceServer.ResolveUseLocal(config, ["--local"])));
    }

    private static void WithLocalEnv(string? value, Action body)
    {
        var previous = Environment.GetEnvironmentVariable("SUTANDO_VOICE_LOCAL");
        Environment.SetEnvironmentVariable("SUTANDO_VOICE_LOCAL", value);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUTANDO_VOICE_LOCAL", previous);
        }
    }

    private static void WithoutLocalEnv(Action body) => WithLocalEnv(null, body);
}
