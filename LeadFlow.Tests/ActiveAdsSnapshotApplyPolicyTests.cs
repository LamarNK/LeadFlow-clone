using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class ActiveAdsSnapshotApplyPolicyTests
{
    [Fact]
    public void ShouldSkip_When_TabShowsItems_But_VacancyRowsEmpty_And_OldSnapshotNonEmpty()
    {
        var s = new ProfileResult
        {
            ParseSuccess = true,
            PageLoadedSuccessfully = true,
            ActiveTabCounterResolved = true,
            ActiveCount = 160,
            ItemSnippetMarkersFound = 5,
            ActiveAds = []
        };

        Assert.True(ActiveAdsSnapshotApplyPolicy.ShouldSkipApplyingSnapshot(s, oldAdsCount: 50, newAdsCount: 0, out var r));
        Assert.Equal("tab_active_count_positive_but_zero_vacancy_rows", r);
    }

    [Fact]
    public void ShouldNotSkip_When_ConfirmedZero_TabCounterAndNoMarkers()
    {
        var s = new ProfileResult
        {
            ParseSuccess = true,
            PageLoadedSuccessfully = true,
            ActiveTabCounterResolved = true,
            ActiveCount = 0,
            ItemSnippetMarkersFound = 0,
            ActiveAds = []
        };

        Assert.False(ActiveAdsSnapshotApplyPolicy.ShouldSkipApplyingSnapshot(s, oldAdsCount: 10, newAdsCount: 0, out _));
    }

    [Fact]
    public void ShouldSkip_When_ActiveCountZero_But_TabCounterNotResolved()
    {
        var s = new ProfileResult
        {
            ParseSuccess = true,
            PageLoadedSuccessfully = true,
            ActiveTabCounterResolved = false,
            ActiveCount = 0,
            ItemSnippetMarkersFound = 0,
            ActiveAds = []
        };

        Assert.True(ActiveAdsSnapshotApplyPolicy.ShouldSkipApplyingSnapshot(s, oldAdsCount: 10, newAdsCount: 0, out var r));
        Assert.Equal("active_tab_counter_not_found_in_html", r);
    }

    [Fact]
    public void ShouldSkip_When_MarkersPresent_But_NoVacancyRows()
    {
        var s = new ProfileResult
        {
            ParseSuccess = true,
            PageLoadedSuccessfully = true,
            ActiveTabCounterResolved = true,
            ActiveCount = 0,
            ItemSnippetMarkersFound = 3,
            ActiveAds = []
        };

        Assert.True(ActiveAdsSnapshotApplyPolicy.ShouldSkipApplyingSnapshot(s, oldAdsCount: 10, newAdsCount: 0, out var r));
        Assert.Equal("item_snippet_markers_present_but_zero_vacancy_rows", r);
    }

    [Fact]
    public void ShouldNotSkip_When_OldEmpty_Or_NewNonZero()
    {
        var s = new ProfileResult { ParseSuccess = true, ActiveCount = 999, ActiveAds = [new()] };

        Assert.False(ActiveAdsSnapshotApplyPolicy.ShouldSkipApplyingSnapshot(s, oldAdsCount: 0, newAdsCount: 1, out _));
        Assert.False(ActiveAdsSnapshotApplyPolicy.ShouldSkipApplyingSnapshot(s, oldAdsCount: 5, newAdsCount: 3, out _));
    }
}
