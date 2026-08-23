using LeadFlow.Core.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoHumanVariationTests
{
    [Fact]
    public void RollPermille_Zero_IsNeverTrue() =>
        Assert.False(AvitoHumanVariation.RollPermille(0, draw: 0));

    [Fact]
    public void RollPermille_Thousand_IsAlwaysTrue() =>
        Assert.True(AvitoHumanVariation.RollPermille(1000, draw: 999));

    [Theory]
    [InlineData(200, 199, true)]
    [InlineData(200, 200, false)]
    public void RollPermille_UsesExclusiveUpperDraw(int permille, int draw, bool expected) =>
        Assert.Equal(expected, AvitoHumanVariation.RollPermille(permille, draw));

    [Fact]
    public void NextInclusive_StaysInsideBounds()
    {
        for (var i = 0; i < 40; i++)
        {
            var value = AvitoHumanVariation.NextInclusive(6, 10);
            Assert.InRange(value, 6, 10);
        }
    }

    [Fact]
    public void NextPhoneRevealBudget_IsBetweenMinAndMax()
    {
        for (var i = 0; i < 20; i++)
        {
            Assert.InRange(
                AvitoHumanVariation.NextPhoneRevealBudget(),
                MonitoringTiming.MinPhoneRevealsPerSubProfilePerCycle,
                MonitoringTiming.MaxPhoneRevealsPerSubProfilePerCycle);
        }
    }

    [Fact]
    public void NextAutoReplyBudget_IsBetweenMinAndMax()
    {
        for (var i = 0; i < 20; i++)
        {
            Assert.InRange(
                AvitoHumanVariation.NextAutoReplyBudget(),
                MonitoringTiming.MinMessengerAutoRepliesPerSubProfilePerCycle,
                MonitoringTiming.MaxMessengerAutoRepliesPerSubProfilePerCycle);
        }
    }

    [Fact]
    public void Shuffle_IsPermutationOfInput()
    {
        var source = new[] { "a", "b", "c", "d", "e" };
        var copy = source.ToList();
        AvitoHumanVariation.Shuffle(copy);

        Assert.Equal(source.Length, copy.Count);
        Assert.Equal(source.OrderBy(x => x), copy.OrderBy(x => x));
    }

    [Fact]
    public void ChanceConstants_ArePartialNotCertain()
    {
        Assert.InRange(MonitoringTiming.SkipBalanceChancePermille, 1, 499);
        Assert.InRange(MonitoringTiming.ScrollBackChancePermille, 1, 499);
        Assert.InRange(MonitoringTiming.ItemsLingerChancePermille, 1, 700);
        Assert.InRange(MonitoringTiming.MouseWanderChancePermille, 1, 700);
        Assert.InRange(MonitoringTiming.ExtraSubProfilePauseChancePermille, 1, 499);
    }
}
