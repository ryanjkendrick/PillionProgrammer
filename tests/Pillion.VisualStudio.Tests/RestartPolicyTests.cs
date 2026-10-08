using System;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

public class RestartPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Defaults_AreThreeCrashesInFiveMinutes()
    {
        var policy = new RestartPolicy();

        Assert.Equal(3, policy.MaxCrashes);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.Window);
    }

    [Fact]
    public void FirstTwoCrashes_BackOffOneThenFiveSeconds()
    {
        var policy = new RestartPolicy();

        var first = policy.RecordCrash(T0);
        var second = policy.RecordCrash(T0.AddSeconds(10));

        Assert.False(first.GiveUp);
        Assert.Equal(TimeSpan.FromSeconds(1), first.Delay);
        Assert.Equal(1, first.CrashesInWindow);
        Assert.False(second.GiveUp);
        Assert.Equal(TimeSpan.FromSeconds(5), second.Delay);
        Assert.Equal(2, second.CrashesInWindow);
    }

    [Fact]
    public void ThirdCrashWithinFiveMinutes_GivesUp()
    {
        var policy = new RestartPolicy();
        policy.RecordCrash(T0);
        policy.RecordCrash(T0.AddMinutes(2));

        var third = policy.RecordCrash(T0.AddMinutes(4).AddSeconds(59));

        Assert.True(third.GiveUp);
        Assert.Equal(3, third.CrashesInWindow);
    }

    [Fact]
    public void ThirdCrashJustOutsideTheWindow_RestartsAfterThirtySeconds()
    {
        // Crashes 4 minutes apart: each is "consecutive" (within 5 minutes of the previous one), but never three in
        // one 5-minute window, so the backoff climbs to its 30 s step and stays there.
        var policy = new RestartPolicy();
        policy.RecordCrash(T0);
        policy.RecordCrash(T0.AddMinutes(4));

        var third = policy.RecordCrash(T0.AddMinutes(8));
        var fourth = policy.RecordCrash(T0.AddMinutes(12));

        Assert.False(third.GiveUp);
        Assert.Equal(TimeSpan.FromSeconds(30), third.Delay);
        Assert.Equal(2, third.CrashesInWindow);
        Assert.False(fourth.GiveUp);
        Assert.Equal(TimeSpan.FromSeconds(30), fourth.Delay);
    }

    [Fact]
    public void CrashAfterAStablePeriod_StartsAgainAtOneSecond()
    {
        var policy = new RestartPolicy();
        policy.RecordCrash(T0);
        policy.RecordCrash(T0.AddSeconds(30));

        var later = policy.RecordCrash(T0.AddMinutes(30));

        Assert.False(later.GiveUp);
        Assert.Equal(TimeSpan.FromSeconds(1), later.Delay);
        Assert.Equal(1, later.CrashesInWindow);
    }

    [Fact]
    public void Window_IsSliding()
    {
        var policy = new RestartPolicy();
        policy.RecordCrash(T0);
        policy.RecordCrash(T0.AddMinutes(3));

        // Exactly five minutes after the first crash, the first one has left the window.
        var third = policy.RecordCrash(T0.AddMinutes(5));
        var fourth = policy.RecordCrash(T0.AddMinutes(6));

        Assert.False(third.GiveUp);
        Assert.Equal(2, third.CrashesInWindow);
        Assert.True(fourth.GiveUp);
    }

    [Fact]
    public void Reset_ForgetsEveryCrash()
    {
        var policy = new RestartPolicy();
        policy.RecordCrash(T0);
        policy.RecordCrash(T0.AddSeconds(1));
        Assert.True(policy.RecordCrash(T0.AddSeconds(2)).GiveUp);

        policy.Reset();
        var afterReset = policy.RecordCrash(T0.AddSeconds(3));

        Assert.False(afterReset.GiveUp);
        Assert.Equal(TimeSpan.FromSeconds(1), afterReset.Delay);
        Assert.Equal(1, afterReset.CrashesInWindow);
    }

    [Fact]
    public void Constructor_RejectsAnEmptySchedule()
    {
        Assert.Throws<ArgumentException>(() => new RestartPolicy([], 3, TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestartPolicy([TimeSpan.Zero], 0, TimeSpan.FromMinutes(5)));
    }
}
