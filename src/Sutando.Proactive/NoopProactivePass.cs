using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sutando.Proactive;

/// <summary>
/// Default <see cref="IProactivePass"/> implementation that logs and exits. Useful as a
/// sentinel until the host wires its own pass — confirms the scheduler chassis fires —
/// and as the test-default for pieces of the system that just need "some pass" to be
/// resolvable from DI.
/// </summary>
public sealed class NoopProactivePass : IProactivePass
{
    private readonly ILogger<NoopProactivePass> _logger;

    /// <summary>Initializes a new noop pass.</summary>
    /// <param name="logger">Optional logger.</param>
    public NoopProactivePass(ILogger<NoopProactivePass>? logger = null)
    {
        _logger = logger ?? NullLogger<NoopProactivePass>.Instance;
    }

    /// <inheritdoc />
    public Task RunAsync(ProactivePassContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        _logger.LogInformation(
            "NoopProactivePass invoked at {UtcNow:o} (triggered by {Entry}). Host has not wired a real IProactivePass yet.",
            context.UtcNow,
            context.TriggeringEntry?.Name ?? "(manual)");

        return Task.CompletedTask;
    }
}
