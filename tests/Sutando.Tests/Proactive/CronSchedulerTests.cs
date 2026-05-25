using Microsoft.Extensions.Time.Testing;
using Sutando.Proactive;

namespace Sutando.Tests.Proactive;

/// <summary>
/// Verifies <see cref="CronScheduler"/> fires entries when their cron expressions are due,
/// re-arms after each fire, and survives callback exceptions.
/// </summary>
public sealed class CronSchedulerTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 5, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Start_WithDueEntry_FiresOnAdvance()
    {
        // Cron "* * * * *" fires every minute. From 00:00:00, the next fire is 00:01:00.
        var time = new FakeTimeProvider(StartUtc);
        var scheduler = new CronScheduler(time);
        var fires = new List<string>();

        scheduler.Start(
            new[] { new CronEntry("every-minute", "* * * * *", Prompt: "x") },
            (e, _) =>
            {
                lock (fires)
                {
                    fires.Add(e.Name);
                }
                return Task.CompletedTask;
            },
            CancellationToken.None);

        // Nothing fires before the minute mark.
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(fires);

        // The minute mark fires once …
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(fires);

        // … and the scheduler re-arms — next fire is at +2 min.
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, fires.Count);

        scheduler.Stop();
    }

    [Fact]
    public void Start_MultipleEntries_FireIndependently()
    {
        var time = new FakeTimeProvider(StartUtc);
        var scheduler = new CronScheduler(time);
        var fires = new List<string>();

        scheduler.Start(
            new[]
            {
                new CronEntry("every-min", "* * * * *", Prompt: "a"),
                new CronEntry("every-5-min", "*/5 * * * *", Prompt: "b"),
            },
            (e, _) =>
            {
                lock (fires)
                {
                    fires.Add(e.Name);
                }
                return Task.CompletedTask;
            },
            CancellationToken.None);

        // Advance 5 minutes — every-min fires 5×, every-5-min fires 1× (at +5:00).
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(5, fires.Count(n => n == "every-min"));
        Assert.Equal(1, fires.Count(n => n == "every-5-min"));

        scheduler.Stop();
    }

    [Fact]
    public void Start_CallbackException_DoesNotKillScheduler()
    {
        var time = new FakeTimeProvider(StartUtc);
        var scheduler = new CronScheduler(time);
        var fires = 0;

        scheduler.Start(
            new[] { new CronEntry("boomer", "* * * * *", Prompt: "x") },
            (_, _) =>
            {
                Interlocked.Increment(ref fires);
                throw new InvalidOperationException("intentional");
            },
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(3));

        // Three fires, three exceptions — scheduler still re-arms after each.
        Assert.Equal(3, fires);
        scheduler.Stop();
    }

    [Fact]
    public void Start_InvalidCronExpression_SkipsEntryButKeepsOthers()
    {
        var time = new FakeTimeProvider(StartUtc);
        var scheduler = new CronScheduler(time);
        var fires = new List<string>();

        scheduler.Start(
            new[]
            {
                new CronEntry("bad", "not a cron", Prompt: "x"),
                new CronEntry("good", "* * * * *", Prompt: "y"),
            },
            (e, _) =>
            {
                lock (fires)
                {
                    fires.Add(e.Name);
                }
                return Task.CompletedTask;
            },
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(2, fires.Count);
        Assert.All(fires, n => Assert.Equal("good", n));

        scheduler.Stop();
    }

    [Fact]
    public void Stop_HaltsAllPendingTimers()
    {
        var time = new FakeTimeProvider(StartUtc);
        var scheduler = new CronScheduler(time);
        var fires = 0;

        scheduler.Start(
            new[] { new CronEntry("every-min", "* * * * *", Prompt: "x") },
            (_, _) =>
            {
                Interlocked.Increment(ref fires);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, fires);

        scheduler.Stop();
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, fires);
    }

    [Fact]
    public void Start_HonoursCancellationToken()
    {
        var time = new FakeTimeProvider(StartUtc);
        var scheduler = new CronScheduler(time);
        using var cts = new CancellationTokenSource();
        var fires = 0;

        scheduler.Start(
            new[] { new CronEntry("every-min", "* * * * *", Prompt: "x") },
            (_, _) =>
            {
                Interlocked.Increment(ref fires);
                return Task.CompletedTask;
            },
            cts.Token);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, fires);

        cts.Cancel();
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, fires);
    }
}
