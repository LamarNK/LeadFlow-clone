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

    [Fact]
    public void PublishableUnpublishedAd_ExposesRenewalActionAndStateTransitions()
    {
        var item = new DashboardAdDisplayItem(
            DashboardAdKind.Unpublished,
            new AvitoAdStatus
            {
                Id = "8140706797",
                SourceTab = AvitoAdStatus.UnpublishedTab,
                CanPublish = true
            });

        Assert.True(item.ShowRenewalAction);
        Assert.True(item.CanStartRenewal);
        Assert.Equal("Опубликовать на 30 дней", item.RenewalButtonCaption);

        item.SetRenewalRunning();

        Assert.True(item.IsRenewalRunning);
        Assert.False(item.CanStartRenewal);
        Assert.Equal("Публикуем…", item.RenewalButtonCaption);

        item.SetRenewalResult(AvitoAdRenewalResult.Failed("test", "Avito вернул ошибку"));

        Assert.True(item.IsRenewalFailed);
        Assert.True(item.CanStartRenewal);
        Assert.Equal("Повторить публикацию", item.RenewalButtonCaption);
        Assert.Equal("Avito вернул ошибку", item.RenewalMessage);
    }

    [Fact]
    public void WaitingUnpublishedAd_ShowsPendingExplanationInsteadOfAction()
    {
        var item = new DashboardAdDisplayItem(
            DashboardAdKind.Unpublished,
            new AvitoAdStatus
            {
                SourceTab = AvitoAdStatus.UnpublishedTab,
                Status = "Ожидает публикации",
                CanPublish = false
            });

        Assert.False(item.ShowRenewalAction);
        Assert.True(item.ShowRenewalUnavailable);
        Assert.Contains("ожидаем", item.RenewalUnavailableText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonAdsPowerUnpublishedAd_ExplainsWhyAutomationIsUnavailable()
    {
        var item = new DashboardAdDisplayItem(
            DashboardAdKind.Unpublished,
            new AvitoAdStatus
            {
                SourceTab = AvitoAdStatus.UnpublishedTab,
                CanPublish = true
            },
            supportsRenewalAutomation: false);

        Assert.False(item.ShowRenewalAction);
        Assert.True(item.ShowRenewalUnavailable);
        Assert.Contains("AdsPower", item.RenewalUnavailableText, StringComparison.Ordinal);
    }
}
