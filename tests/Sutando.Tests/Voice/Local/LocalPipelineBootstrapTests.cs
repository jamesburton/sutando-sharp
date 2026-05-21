using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Voice;

namespace Sutando.Tests.Voice.Local;

/// <summary>
/// Tests for the fail-graceful contract of the <c>--local</c> voice boot path. A missing model
/// file must surface as an operator-actionable <see cref="LocalPipelineConfigurationException"/>,
/// not a host crash. The transport factory then turns that into a per-session error envelope.
/// </summary>
public sealed class LocalPipelineBootstrapTests
{
    [Fact]
    public void Build_throws_clear_error_when_whisper_model_is_unset()
    {
        var options = new VoiceOptions { UseLocal = true };

        var ex = Assert.Throws<LocalPipelineConfigurationException>(
            () => LocalPipelineBootstrap.Build(options, NullLoggerFactory.Instance));

        Assert.Contains("Whisper STT model", ex.Message);
        Assert.Contains("SUTANDO_WHISPER_MODEL", ex.Message);
    }

    [Fact]
    public void Build_throws_clear_error_when_a_model_path_points_at_a_missing_file()
    {
        var options = new VoiceOptions { UseLocal = true };
        options.LocalModels.WhisperModel = Path.Combine(Path.GetTempPath(), "definitely-not-here.bin");

        var ex = Assert.Throws<LocalPipelineConfigurationException>(
            () => LocalPipelineBootstrap.Build(options, NullLoggerFactory.Instance));

        Assert.Contains("was not found", ex.Message);
        Assert.Contains("definitely-not-here.bin", ex.Message);
    }

    [Fact]
    public void TransportFactory_does_not_throw_when_models_are_missing()
    {
        // The factory must capture the configuration error rather than propagate it — a
        // misconfigured --local must not stop the host from binding.
        var options = new VoiceOptions { UseLocal = true };

        var factory = new LocalPipelineTransportFactory(options, NullLoggerFactory.Instance);

        // Create() still returns a usable IRealtimeClient — an "unavailable" one that surfaces
        // the error per-session.
        var client = factory.Create();
        Assert.NotNull(client);
    }
}
