using System;
using Wholphin.Engine.Data.Entities;
using Wholphin.Engine.Data.Enums;
using Wholphin.Engine.Discovery;
using Xunit;

namespace Wholphin.Engine.Tests;

/// <summary>Unit tests for the recommendation-memory interest math.</summary>
public class InterestModelTests
{
    private static UserItemMemory Fresh(DateTime now) => new()
    {
        UserId = Guid.NewGuid(),
        TmdbId = 42,
        MediaType = MediaType.Movie,
        InterestScore = 1.0,
        UpdatedAt = now,
    };

    [Fact]
    public void EffectiveInterest_NullMemory_IsFull()
    {
        Assert.Equal(1.0, InterestModel.EffectiveInterest(null, DateTime.UtcNow));
    }

    [Fact]
    public void OnIgnoredCycle_DecaysByFactor()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var memory = Fresh(now);

        InterestModel.OnIgnoredCycle(memory, now);

        Assert.Equal(InterestModel.IgnoredCycleDecay, memory.InterestScore, 6);
        Assert.Null(memory.CooldownUntil);
    }

    [Fact]
    public void OnIgnoredCycle_StartsCooldownBelowThreshold()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var memory = Fresh(now);

        // 1.0 -> 0.7 -> 0.49 -> 0.343 (< 0.35) triggers cooldown on the third ignored cycle.
        InterestModel.OnIgnoredCycle(memory, now);
        InterestModel.OnIgnoredCycle(memory, now);
        Assert.Null(memory.CooldownUntil);

        InterestModel.OnIgnoredCycle(memory, now);
        Assert.True(memory.InterestScore < InterestModel.CooldownThreshold);
        Assert.NotNull(memory.CooldownUntil);
        Assert.True(InterestModel.IsInCooldown(memory, now));
    }

    [Fact]
    public void OnEngaged_Request_ResetsInterestAndClearsCooldown()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var memory = Fresh(now);
        memory.InterestScore = 0.2;
        memory.CooldownUntil = now.AddDays(5);

        InterestModel.OnEngaged(memory, BehaviorEventType.RequestCreated, now);

        Assert.Equal(1.0, memory.InterestScore, 6);
        Assert.Null(memory.CooldownUntil);
        Assert.False(InterestModel.IsInCooldown(memory, now));
    }

    [Fact]
    public void OnEngaged_Click_RestoresAdditively()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var memory = Fresh(now);
        memory.InterestScore = 0.4;

        InterestModel.OnEngaged(memory, BehaviorEventType.CardClicked, now);

        // 0.4 + 0.3 boost = 0.7.
        Assert.Equal(0.7, memory.InterestScore, 6);
    }

    [Fact]
    public void EffectiveInterest_RecoversWithTime()
    {
        var updated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var memory = Fresh(updated);
        memory.InterestScore = 0.7;

        // One half-life later, half the lost interest (0.3) has healed: 1 - 0.3*0.5 = 0.85.
        var later = updated.AddDays(InterestModel.RecoveryHalfLifeDays);
        Assert.Equal(0.85, InterestModel.EffectiveInterest(memory, later), 6);
    }

    [Fact]
    public void ExposurePenalty_ScalesAndCaps()
    {
        Assert.Equal(0.0, InterestModel.ExposurePenalty(null));

        var memory = Fresh(DateTime.UtcNow);
        memory.TimesRecommended = 2;
        Assert.Equal(0.10, InterestModel.ExposurePenalty(memory), 6);

        memory.TimesRecommended = 99;
        Assert.Equal(InterestModel.ExposurePenaltyStep * InterestModel.ExposurePenaltyCap, InterestModel.ExposurePenalty(memory), 6);
    }

    [Fact]
    public void OnImpression_CountsAndPreservesRecovery()
    {
        var updated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var memory = Fresh(updated);
        memory.InterestScore = 0.5;
        var later = updated.AddDays(InterestModel.RecoveryHalfLifeDays);

        var expected = InterestModel.EffectiveInterest(memory, later);
        InterestModel.OnImpression(memory, later);

        Assert.Equal(1, memory.Impressions);
        // Materialized effective, so bumping UpdatedAt didn't erase healing progress.
        Assert.Equal(expected, memory.InterestScore, 6);
        Assert.Equal(later, memory.UpdatedAt);
    }
}
