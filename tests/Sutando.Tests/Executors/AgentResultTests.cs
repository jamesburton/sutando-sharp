using Sutando.Core;

namespace Sutando.Tests.Executors;

public sealed class AgentResultTests
{
    [Fact]
    public void Ok_SetsBodyAndDurationDefaultsClean()
    {
        var r = AgentResult.Ok("done", TimeSpan.FromSeconds(1));
        Assert.Equal("done", r.Body);
        Assert.False(r.IsError);
        Assert.False(r.TimedOut);
        Assert.False(r.NoSend);
        Assert.False(r.AlreadyReplied);
        Assert.Null(r.DedupedTo);
        Assert.Empty(r.Attachments);
    }

    [Fact]
    public void Error_MarksError()
    {
        var r = AgentResult.Error("bad", TimeSpan.FromMilliseconds(500));
        Assert.True(r.IsError);
        Assert.False(r.TimedOut);
        Assert.Equal("bad", r.Body);
    }

    [Fact]
    public void Timeout_FlagsBothErrorAndTimedOut()
    {
        var r = AgentResult.Timeout(TimeSpan.FromMinutes(5));
        Assert.True(r.IsError);
        Assert.True(r.TimedOut);
        Assert.Contains("00:05:00", r.Body);
    }

    [Fact]
    public void Deduped_SetsTargetWithoutBody()
    {
        var r = AgentResult.Deduped("task-other", TimeSpan.Zero);
        Assert.Equal("task-other", r.DedupedTo);
        Assert.Empty(r.Body);
    }
}
