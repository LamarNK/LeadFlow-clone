using LeadFlow.Core.Models;
using LeadFlow.ViewModels;
using Xunit;

namespace LeadFlow.Tests;

public sealed class DashboardAdDisplayItemTests
{
    [Fact]
    public void UnpublishedAd_UsesUnpublishedBadgeAndPreservesErrorReason()
    {
        var ad = new AvitoAdStatus
        {
            Id = "42",
            Title = "Водитель",
            SourceTab = AvitoAdStatus.UnpublishedTab,
            Status = "Ошибки автопубликации",
            ErrorReason = "Не заполнено поле зарплаты",
            CanPublish = false
        };

        var item = new DashboardAdDisplayItem(DashboardAdKind.Unpublished, ad);

        Assert.True(item.IsUnpublishedPresentation);
        Assert.Equal(DashboardAdBadgeKind.Unpublished, item.PrimaryBadgeKind);
        Assert.Equal("Не опубликовано", item.StatusBadgeCaption);
        Assert.Equal("Не заполнено поле зарплаты", item.Ad.ErrorReason);
    }

    [Fact]
    public void BlockedAd_UsesBlockedBadgeEvenWhenErrorReasonIsPresent()
    {
        var ad = new AvitoAdStatus
        {
            Id = "43",
            SourceTab = AvitoAdStatus.ErrorTab,
            Status = "Отклонено",
            ErrorReason = "Нарушение правил"
        };

        var item = new DashboardAdDisplayItem(DashboardAdKind.Blocked, ad);

        Assert.Equal(DashboardAdBadgeKind.Blocked, item.PrimaryBadgeKind);
        Assert.Equal("Заблокировано", item.StatusBadgeCaption);
    }
}
