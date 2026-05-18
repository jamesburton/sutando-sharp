using Sutando.Bridge;

namespace Sutando.Tests;

public sealed class TaskFileTests
{
    [Fact]
    public void Parse_RequiredFields()
    {
        var raw = """
            id: task-chat-1747500000
            timestamp: 2026-05-18T12:34:56Z
            task: write a summary of the user's last 3 emails
            source: chat
            channel_id: local-chat
            user_id: chat-local
            access_tier: owner
            priority: normal
            """;

        var env = TaskFile.Parse(raw);

        Assert.Equal("task-chat-1747500000", env.Id);
        Assert.Equal("write a summary of the user's last 3 emails", env.Body);
        Assert.Equal(TaskSource.Chat, env.Source);
        Assert.Equal("local-chat", env.ChannelId);
        Assert.Equal("chat-local", env.UserId);
        Assert.Equal(AccessTier.Owner, env.AccessTier);
        Assert.Equal(TaskPriority.Normal, env.Priority);
        Assert.Equal(new DateTimeOffset(2026, 5, 18, 12, 34, 56, TimeSpan.Zero), env.Timestamp);
    }

    [Fact]
    public void Parse_MultiLineTaskBody_PreservesNewlines()
    {
        var raw = """
            id: task-chat-1
            timestamp: 2026-05-18T00:00:00Z
            task: first line
            second line — body continues
            and a third
            source: chat
            channel_id: local-chat
            user_id: chat-local
            access_tier: owner
            priority: normal
            """;

        var env = TaskFile.Parse(raw);
        Assert.Equal("first line\nsecond line — body continues\nand a third", env.Body);
    }

    [Fact]
    public void Parse_UnknownFieldIsTreatedAsBodyContinuation()
    {
        // A note: prefix inside the body must not be mistaken for a new key.
        var raw = """
            id: task-chat-1
            timestamp: 2026-05-18T00:00:00Z
            task: read this email
            note: this part is body text not metadata
            source: chat
            channel_id: local-chat
            user_id: chat-local
            access_tier: owner
            priority: normal
            """;

        var env = TaskFile.Parse(raw);
        Assert.Contains("note: this part is body text not metadata", env.Body);
    }

    [Fact]
    public void Parse_MetaFields_AreCaptured()
    {
        var raw = """
            id: task-discord-1
            timestamp: 2026-05-18T00:00:00Z
            task: do the thing
            source: discord
            channel_id: 123456789
            user_id: 999
            access_tier: team
            priority: low
            meta.guild_id: 555
            meta.thread_id: 777
            """;

        var env = TaskFile.Parse(raw);
        Assert.Equal("555", env.Meta["guild_id"]);
        Assert.Equal("777", env.Meta["thread_id"]);
    }

    [Fact]
    public void Parse_Missing_Required_Field_Throws()
    {
        var raw = """
            id: task-1
            timestamp: 2026-05-18T00:00:00Z
            task: hi
            """;

        Assert.Throws<FormatException>(() => TaskFile.Parse(raw));
    }

    [Fact]
    public void Parse_Unknown_Source_Throws()
    {
        var raw = """
            id: task-1
            timestamp: 2026-05-18T00:00:00Z
            task: hi
            source: pigeon
            channel_id: c
            user_id: u
            access_tier: owner
            priority: normal
            """;

        Assert.Throws<FormatException>(() => TaskFile.Parse(raw));
    }

    [Fact]
    public void Serialize_RoundTripsParse()
    {
        var original = new TaskEnvelope
        {
            Id = "task-discord-42",
            Timestamp = new DateTimeOffset(2026, 5, 18, 1, 2, 3, TimeSpan.Zero),
            Body = "multi\nline\nbody",
            Source = TaskSource.Discord,
            ChannelId = "123",
            UserId = "456",
            AccessTier = AccessTier.Team,
            Priority = TaskPriority.Low,
            Timeout = TimeSpan.FromMinutes(5),
            DmOnTimeout = true,
            ReplyToMessageId = "msg-99",
            Meta = new Dictionary<string, string>
            {
                ["guild_id"] = "g",
                ["thread_id"] = "t",
            },
        };

        var serialized = TaskFile.Serialize(original);
        var parsed = TaskFile.Parse(serialized);

        Assert.Equal(original.Id, parsed.Id);
        Assert.Equal(original.Timestamp, parsed.Timestamp);
        Assert.Equal(original.Body, parsed.Body);
        Assert.Equal(original.Source, parsed.Source);
        Assert.Equal(original.ChannelId, parsed.ChannelId);
        Assert.Equal(original.UserId, parsed.UserId);
        Assert.Equal(original.AccessTier, parsed.AccessTier);
        Assert.Equal(original.Priority, parsed.Priority);
        Assert.Equal(original.Timeout, parsed.Timeout);
        Assert.Equal(original.DmOnTimeout, parsed.DmOnTimeout);
        Assert.Equal(original.ReplyToMessageId, parsed.ReplyToMessageId);
        Assert.Equal(original.Meta["guild_id"], parsed.Meta["guild_id"]);
        Assert.Equal(original.Meta["thread_id"], parsed.Meta["thread_id"]);
    }

    [Fact]
    public void CancelInstruction_IsDetected()
    {
        var env = new TaskEnvelope
        {
            Id = "task-voice-cancel-1",
            Timestamp = DateTimeOffset.UtcNow,
            Body = "CANCEL_INSTRUCTION: task-chat-42\nreason: user said stop",
            Source = TaskSource.Voice,
            ChannelId = "local-voice",
            UserId = "owner",
            AccessTier = AccessTier.Owner,
            Priority = TaskPriority.Urgent,
        };

        Assert.True(env.IsCancelInstruction);
        Assert.Equal("task-chat-42", env.CancelTargetId);
    }

    [Theory]
    [InlineData(TaskSource.Voice, AccessTier.Owner, TaskPriority.Urgent)]
    [InlineData(TaskSource.Phone, AccessTier.Verified, TaskPriority.Urgent)]
    [InlineData(TaskSource.Chat, AccessTier.Owner, TaskPriority.Normal)]
    [InlineData(TaskSource.Api, AccessTier.Verified, TaskPriority.Normal)]
    [InlineData(TaskSource.Cron, AccessTier.Owner, TaskPriority.Low)]
    [InlineData(TaskSource.Telegram, AccessTier.Other, TaskPriority.Low)]
    public void DefaultPriorityMatchesUpstream(TaskSource src, AccessTier tier, TaskPriority expected)
    {
        Assert.Equal(expected, TaskPriorities.DefaultFor(src, tier));
    }
}
