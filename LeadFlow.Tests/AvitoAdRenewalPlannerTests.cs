using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAdRenewalPlannerTests
{
    [Fact]
    public void Prepare_UnpublishedWithPublishAction_IsEligible()
    {
        var ad = new AvitoAdStatus
        {
            Id = "8140037674",
            Title = "Охранник",
            SourceTab = AvitoAdStatus.UnpublishedTab,
            CanPublish = true
        };

        var plan = AvitoAdRenewalPlanner.Prepare(ad);

        Assert.True(plan.IsEligible);
        Assert.Equal("ready_for_explicit_publish", plan.Reason);
        Assert.Equal("publish-action", plan.ActionMarker);
    }

    [Fact]
    public void PrepareWithoutCapability_ReturnsReasonAndDoesNotEnableAction()
    {
        var ad = new AvitoAdStatus
        {
            Id = "1",
            SourceTab = AvitoAdStatus.UnpublishedTab,
            CanPublish = false
        };

        var plan = AvitoAdRenewalPlanner.Prepare(ad);

        Assert.False(plan.IsEligible);
        Assert.Equal("publish_action_not_available", plan.Reason);
        Assert.Empty(plan.ActionMarker);
    }

    [Fact]
    public void Prepare_ActiveAd_IsNeverEligibleForRenewal()
    {
        var ad = new AvitoAdStatus
        {
            Id = "2",
            SourceTab = AvitoAdStatus.ActiveTab,
            CanPublish = true
        };

        var plan = AvitoAdRenewalPlanner.Prepare(ad);

        Assert.False(plan.IsEligible);
        Assert.Equal("listing_is_not_unpublished", plan.Reason);
        Assert.Empty(plan.ActionMarker);
    }

    [Fact]
    public void PrepareMany_ReturnsOnePlanPerAdInInputOrder()
    {
        var ads = new[]
        {
            new AvitoAdStatus { Id = "1", SourceTab = AvitoAdStatus.UnpublishedTab, CanPublish = true },
            new AvitoAdStatus { Id = "2", SourceTab = AvitoAdStatus.ErrorTab, CanPublish = true }
        };

        var plans = AvitoAdRenewalPlanner.PrepareMany(ads);

        Assert.Collection(
            plans,
            first => Assert.Equal("1", first.AvitoItemId),
            second =>
            {
                Assert.Equal("2", second.AvitoItemId);
                Assert.False(second.IsEligible);
            });
    }
}
