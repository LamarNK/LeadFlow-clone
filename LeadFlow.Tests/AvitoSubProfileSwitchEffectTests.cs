using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoSubProfileSwitchEffectTests
{
    [Fact]
    public void ShouldClick_WhenModalOpenAndTargetIsNotCurrent()
    {
        var snapshot = new AvitoSubProfileSwitchSnapshot(
            ModalOpen: true,
            CardsCount: 3,
            TargetCardFound: true,
            CurrentSubProfileId: "111",
            CurrentSubProfileName: "текущий",
            Url: "https://www.avito.ru/profile/pro/items#profile/switch?withEntities=true");

        Assert.True(AvitoSubProfileSwitchEffect.ShouldClickTarget(snapshot, "222"));
        Assert.False(AvitoSubProfileSwitchEffect.IsAlreadyCurrent(snapshot, "222"));
        Assert.False(AvitoSubProfileSwitchEffect.IsSuccessfulSwitch(snapshot, "222"));
    }

    [Fact]
    public void AlreadyCurrent_RequiresTargetCardAndMatchingId()
    {
        var current = new AvitoSubProfileSwitchSnapshot(
            true, 2, true, "222", "цель", "https://www.avito.ru/profile/dashboard#profile/switch");
        Assert.True(AvitoSubProfileSwitchEffect.IsAlreadyCurrent(current, "222"));
        Assert.False(AvitoSubProfileSwitchEffect.ShouldClickTarget(current, "222"));

        var missingCard = current with { TargetCardFound = false };
        Assert.False(AvitoSubProfileSwitchEffect.IsAlreadyCurrent(missingCard, "222"));
    }

    [Fact]
    public void CardNotFound_DoesNotClick()
    {
        var snapshot = new AvitoSubProfileSwitchSnapshot(
            true, 4, false, "111", "текущий", "https://www.avito.ru/profile/dashboard#profile/switch");

        Assert.False(AvitoSubProfileSwitchEffect.ShouldClickTarget(snapshot, "999"));
        Assert.False(AvitoSubProfileSwitchEffect.IsAlreadyCurrent(snapshot, "999"));
        Assert.False(AvitoSubProfileSwitchEffect.IsSuccessfulSwitch(snapshot, "999"));
    }

    [Fact]
    public void ClickReportedSuccess_ButDomUnchanged_IsNotASwitch()
    {
        var before = new AvitoSubProfileSwitchSnapshot(
            true, 3, true, "111", "текущий", "https://www.avito.ru/profile/pro/items#profile/switch?withEntities=true");
        var after = before;

        Assert.True(AvitoSubProfileSwitchEffect.ClickReportedSuccessWithoutDomChange(
            before, after, "222", clickReportedSuccess: true));
        Assert.False(AvitoSubProfileSwitchEffect.IsSuccessfulSwitch(after, "222"));
    }

    [Fact]
    public void SuccessfulSwitch_RequiresCurrentIdAndClosedModal()
    {
        var afterClickStillOpen = new AvitoSubProfileSwitchSnapshot(
            true, 3, true, "222", "цель", "https://www.avito.ru/profile/dashboard#profile/switch");
        Assert.True(AvitoSubProfileSwitchEffect.TargetBecameCurrent(afterClickStillOpen, "222"));
        Assert.False(AvitoSubProfileSwitchEffect.IsSuccessfulSwitch(afterClickStillOpen, "222"));

        var closed = afterClickStillOpen with { ModalOpen = false, CardsCount = 0, TargetCardFound = false };
        Assert.True(AvitoSubProfileSwitchEffect.IsSuccessfulSwitch(closed, "222"));
    }

    [Fact]
    public void ModalClosedPredicate_DoesNotUseRoleDialogOr()
    {
        var js = AvitoSubProfileSwitchEffect.BuildModalClosedPredicateJs();
        Assert.Contains("component-profile-switch/root", js, StringComparison.Ordinal);
        Assert.DoesNotContain("role='dialog'", js, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"dialog\"", js, StringComparison.Ordinal);
        Assert.DoesNotContain("||", js, StringComparison.Ordinal);
    }

    [Fact]
    public void OldDialogOrPredicate_WouldSucceedWhileModalStillOpen()
    {
        const bool rootPresent = true;
        const bool dialogPresent = false;
        var oldPredicate = !rootPresent || !dialogPresent;
        Assert.True(oldPredicate);
        Assert.False(AvitoSubProfileSwitchEffect.IsSuccessfulSwitch(
            new AvitoSubProfileSwitchSnapshot(true, 2, true, "111", "текущий", null),
            "222"));
    }

    [Fact]
    public void ParseSnapshot_ReadsCardsAndCurrent()
    {
        const string json = """
            {
              "modalOpen": true,
              "cardsCount": 7,
              "targetCardFound": true,
              "currentSubProfileId": "434877613",
              "currentSubProfileName": "Служба России 3",
              "url": "https://www.avito.ru/profile/pro/items#profile/switch?withEntities=true"
            }
            """;

        var snapshot = AvitoSubProfileSwitchEffect.ParseSnapshot(json);
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.ModalOpen);
        Assert.Equal(7, snapshot.CardsCount);
        Assert.True(snapshot.TargetCardFound);
        Assert.Equal("434877613", snapshot.CurrentSubProfileId);
        Assert.True(AvitoSubProfileSwitchEffect.ShouldClickTarget(snapshot, "434874096"));
    }

    [Fact]
    public void SnapshotScript_TargetsNumericMarkerAndIsCurrentClass()
    {
        var script = AvitoSubProfileSwitchEffect.BuildSnapshotScript("434874096");
        Assert.Contains("component-profile-switch/profile-", script, StringComparison.Ordinal);
        Assert.Contains("434874096", script, StringComparison.Ordinal);
        Assert.Contains("isCurrent", script, StringComparison.Ordinal);
        Assert.Contains("modalOpen", script, StringComparison.Ordinal);
        Assert.Contains("targetCardFound", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomationService_DoesNotWaitForDialogOrPredicate()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LeadFlow.Core",
            "Services",
            "AdsPower",
            "AdsPowerAvitoAutomationService.cs");
        path = Path.GetFullPath(path);
        Assert.True(File.Exists(path), path);
        var source = File.ReadAllText(path);
        Assert.DoesNotContain(
            "!document.querySelector(\"[data-marker='component-profile-switch/root']\") || !document.querySelector(\"[role='dialog']\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains("AvitoHumanPointer.TryClickSelectorAsync", source, StringComparison.Ordinal);
        Assert.Contains("IsSuccessfulSwitch", source, StringComparison.Ordinal);
    }
}

public sealed class AdsPowerCdpGuardTests
{
    [Fact]
    public async Task WaitAsync_WhenTaskNeverCompletes_ThrowsPrefixedTimeout()
    {
        var hung = new TaskCompletionSource<bool>();
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            AdsPowerCdpGuard.WaitAsync(
                hung.Task,
                TimeSpan.FromMilliseconds(40),
                "клик по карточке субпрофиля",
                CancellationToken.None));

        Assert.StartsWith(AdsPowerCdpGuard.TimeoutPrefix, ex.Message, StringComparison.Ordinal);
        Assert.Contains("клик по карточке субпрофиля", ex.Message, StringComparison.Ordinal);
        Assert.True(AdsPowerCdpGuard.IsCdpTimeout(ex));
    }

    [Fact]
    public async Task WaitAsync_WhenTaskCompletes_ReturnsValue()
    {
        var value = await AdsPowerCdpGuard.WaitAsync(
            Task.FromResult(true),
            TimeSpan.FromSeconds(1),
            "проверка модалки",
            CancellationToken.None);
        Assert.True(value);
    }

    [Fact]
    public void IsCdpTimeout_FindsWrappedInnerException()
    {
        var inner = AdsPowerCdpGuard.Timeout("JavaScript-проверка страницы", TimeSpan.FromSeconds(5));
        var wrapped = new InvalidOperationException("session", inner);
        Assert.True(AdsPowerCdpGuard.IsCdpTimeout(wrapped));
        Assert.Same(inner, AdsPowerCdpGuard.FindCdpTimeout(wrapped));
    }

    [Fact]
    public void IsCdpTimeout_IgnoresOrdinaryTimeout()
    {
        Assert.False(AdsPowerCdpGuard.IsCdpTimeout(new TimeoutException("generic")));
        Assert.False(AdsPowerCdpGuard.IsCdpTimeout(new InvalidOperationException("AdsPower не открыл сессию")));
        Assert.False(AdsPowerCdpGuard.IsCdpTimeout(
            new TimeoutException("AdsPower: запуск сессии не завершился за 3 мин.")));
    }

    [Fact]
    public async Task WaitAsync_HungNewPageLikeTask_FailsInsideDiscoveryBudget()
    {
        var hungNewPage = new TaskCompletionSource<bool>();
        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            AdsPowerCdpGuard.WaitAsync(
                hungNewPage.Task,
                TimeSpan.FromMilliseconds(50),
                "поиск рабочей вкладки",
                CancellationToken.None));

        Assert.StartsWith(AdsPowerCdpGuard.TimeoutPrefix, ex.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        Assert.True(AdsPowerCdpGuard.IsCdpTimeout(ex));
    }

    [Fact]
    public async Task WaitAsync_VisuallyAlivePageButCdpHung_StillFreesTheSlot()
    {
        var hungEvaluate = new TaskCompletionSource<string>();
        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<TimeoutException>(() =>
            AdsPowerCdpGuard.WaitAsync(
                hungEvaluate.Task,
                TimeSpan.FromMilliseconds(50),
                "Runtime.evaluate snapshot модалки",
                CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }
}
