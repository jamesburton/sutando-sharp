using System.Threading.Channels;
using Sutando.Realtime;

namespace Sutando.Tests.Realtime;

/// <summary>
/// In-process fake transport. Tests pump <see cref="RealtimeServerEvent"/>s in via
/// <see cref="Emit"/> and assert on the resulting <see cref="VoiceSession.State"/> transitions
/// and the outbound calls (<see cref="SentInputs"/>, <see cref="SentToolResponses"/>).
/// </summary>
internal sealed class FakeRealtimeTransport : IRealtimeTransport
{
    private readonly Channel<RealtimeServerEvent> _events = Channel.CreateUnbounded<RealtimeServerEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public List<RealtimeInput> SentInputs { get; } = new();

    public List<ToolResponse> SentToolResponses { get; } = new();

    public bool ConnectCalled { get; private set; }

    public bool DisconnectCalled { get; private set; }

    public RealtimeSessionConfig? LastConfig { get; private set; }

    public Task ConnectAsync(RealtimeSessionConfig config, CancellationToken ct)
    {
        ConnectCalled = true;
        LastConfig = config;
        return Task.CompletedTask;
    }

    public Task SendRealtimeInputAsync(RealtimeInput input, CancellationToken ct)
    {
        SentInputs.Add(input);
        return Task.CompletedTask;
    }

    public Task SendToolResponseAsync(ToolResponse response, CancellationToken ct)
    {
        SentToolResponses.Add(response);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<RealtimeServerEvent> ReadEventsAsync(CancellationToken ct)
        => _events.Reader.ReadAllAsync(ct);

    public Task DisconnectAsync(CancellationToken ct)
    {
        DisconnectCalled = true;
        _events.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public void Emit(RealtimeServerEvent evt)
    {
        if (!_events.Writer.TryWrite(evt))
        {
            throw new InvalidOperationException("Channel already completed — cannot emit further events.");
        }
    }

    public void Complete() => _events.Writer.TryComplete();
}
