using Sutando.Bridge;

namespace Sutando.Tests;

public sealed class ResultBodyTests
{
    [Fact]
    public void Parse_PlainText_NoMarkers()
    {
        var r = ResultBody.Parse("done — email sent to bob@example.com");
        Assert.Equal("done — email sent to bob@example.com", r.Text);
        Assert.False(r.NoSend);
        Assert.False(r.AlreadyReplied);
        Assert.Null(r.DedupedTo);
        Assert.Empty(r.Attachments);
    }

    [Fact]
    public void Parse_DedupedMarker()
    {
        var r = ResultBody.Parse("[deduped: task-chat-1]\n(unused body)");
        Assert.Equal("task-chat-1", r.DedupedTo);
        Assert.True(r.ShouldSkipDelivery);
        Assert.Equal("(unused body)", r.Text);
    }

    [Fact]
    public void Parse_NoSendMarker()
    {
        var r = ResultBody.Parse("[no-send]\nhandled internally");
        Assert.True(r.NoSend);
        Assert.True(r.ShouldSkipDelivery);
        Assert.Equal("handled internally", r.Text);
    }

    [Fact]
    public void Parse_RepliedMarker()
    {
        var r = ResultBody.Parse("[REPLIED]\nalready sent");
        Assert.True(r.AlreadyReplied);
        Assert.True(r.ShouldSkipDelivery);
    }

    [Fact]
    public void Parse_FileAttachments_AllThreeAliases()
    {
        var r = ResultBody.Parse("[file: /tmp/a.png]\n[send: /tmp/b.pdf]\n[attach: /tmp/c.txt]\nhere are the files");
        Assert.Equal(["/tmp/a.png", "/tmp/b.pdf", "/tmp/c.txt"], r.Attachments);
        Assert.Equal("here are the files", r.Text);
    }

    [Fact]
    public void Parse_StackedMarkers()
    {
        // Multiple markers + an attachment + a body. Order should be tolerated.
        var r = ResultBody.Parse("[REPLIED]\n[file: /tmp/x.png]\nhello");
        Assert.True(r.AlreadyReplied);
        Assert.Single(r.Attachments, "/tmp/x.png");
        Assert.Equal("hello", r.Text);
    }

    [Fact]
    public void Parse_WhitespaceOnly_ProducesEmptyText()
    {
        var r = ResultBody.Parse("   \n  ");
        Assert.Equal(string.Empty, r.Text);
    }

    [Fact]
    public void Parse_UnclosedMarker_TreatedAsBody()
    {
        var r = ResultBody.Parse("[no-send (missing bracket\nbody");
        Assert.False(r.NoSend);
        Assert.StartsWith("[no-send", r.Text);
    }
}
