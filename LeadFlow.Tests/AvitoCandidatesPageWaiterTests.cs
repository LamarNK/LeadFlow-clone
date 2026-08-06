using LeadFlow.Core.Services.Avito;
using System.Diagnostics;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoCandidatesPageWaiterTests
{
    [Fact]
    public async Task CandidatesWithVisibleData_AreReadyBeforeDocumentComplete()
    {
        var targetPolls = 0;

        await AvitoCandidatesPageWaiter.WaitForCandidatesOrThrowFirewallAsync(
            (script, _) =>
            {
                if (script.Contains("const target = \"Candidates\"", StringComparison.Ordinal))
                {
                    targetPolls++;
                    return Task.FromResult(TargetJson(
                        ready: true,
                        reason: "items",
                        readyState: "interactive",
                        hasLoader: true,
                        itemCount: 1));
                }

                return Task.FromResult(PageStateJson());
            },
            null,
            "https://www.avito.ru/profile/candidates",
            CancellationToken.None);

        Assert.Equal(1, targetPolls);
    }

    [Fact]
    public async Task ConfirmedEmptyCandidates_AreReadyWithoutDocumentComplete()
    {
        await AvitoCandidatesPageWaiter.WaitForCandidatesOrThrowFirewallAsync(
            (script, _) => Task.FromResult(script.Contains("const target = \"Candidates\"", StringComparison.Ordinal)
                ? TargetJson(ready: true, reason: "empty", readyState: "loading", hasLoader: false)
                : PageStateJson()),
            null,
            "https://www.avito.ru/profile/candidates",
            CancellationToken.None);
    }

    [Fact]
    public async Task LoaderWithoutData_IsNotAcceptedAsReady()
    {
        var result = await AvitoInteractionWaiter.WaitAsync(
            (script, _) => Task.FromResult(script.Contains("const target = \"Candidates\"", StringComparison.Ordinal)
                ? TargetJson(ready: false, reason: "target_not_available", readyState: "interactive", hasLoader: true)
                : PageStateJson()),
            AvitoInteractionWaiter.Target.Candidates,
            new AvitoInteractionWaiter.Options(25, 5, Operation: "candidates"),
            "https://www.avito.ru/profile/candidates",
            CancellationToken.None);

        Assert.Equal(AvitoInteractionWaiter.Status.TimedOut, result.Status);
        Assert.True(result.HasLoader);
    }

    [Fact]
    public async Task LoginAndCaptcha_AreReturnedBeforeTargetWait()
    {
        var login = await AvitoInteractionWaiter.WaitAsync(
            (_, _) => Task.FromResult(PageStateJson(pageKind: "login", hasLogin: true)),
            AvitoInteractionWaiter.Target.Candidates,
            new AvitoInteractionWaiter.Options(100, 5),
            "https://www.avito.ru/login",
            CancellationToken.None);

        var captcha = await AvitoInteractionWaiter.WaitAsync(
            (_, _) => Task.FromResult(PageStateJson(pageKind: "captcha", hasCaptcha: true)),
            AvitoInteractionWaiter.Target.Candidates,
            new AvitoInteractionWaiter.Options(100, 5),
            "https://www.avito.ru/profile/candidates",
            CancellationToken.None);

        Assert.Equal(AvitoInteractionWaiter.Status.LoginRequired, login.Status);
        Assert.Equal(AvitoInteractionWaiter.Status.CaptchaOrFirewall, captcha.Status);
    }

    [Fact]
    public async Task ProfileItemsAndMessenger_UseTheirOwnReadyContracts()
    {
        var profileItems = await AvitoInteractionWaiter.WaitAsync(
            (script, _) => Task.FromResult(script.Contains("const target = \"ProfileItems\"", StringComparison.Ordinal)
                ? TargetJson(ready: true, reason: "items", readyState: "interactive", hasLoader: true, itemCount: 2)
                : PageStateJson()),
            AvitoInteractionWaiter.Target.ProfileItems,
            new AvitoInteractionWaiter.Options(100, 5),
            "https://www.avito.ru/profile/pro/items",
            CancellationToken.None);

        var messenger = await AvitoInteractionWaiter.WaitAsync(
            (script, _) => Task.FromResult(script.Contains("const target = \"Messenger\"", StringComparison.Ordinal)
                ? TargetJson(ready: true, reason: "messenger", readyState: "loading")
                : PageStateJson()),
            AvitoInteractionWaiter.Target.Messenger,
            new AvitoInteractionWaiter.Options(100, 5),
            "https://www.avito.ru/profile/messenger/channel/1",
            CancellationToken.None);

        Assert.Equal(AvitoInteractionWaiter.Status.Ready, profileItems.Status);
        Assert.Equal(AvitoInteractionWaiter.Status.Ready, messenger.Status);
    }

    [Fact]
    public async Task ProbeException_IsReportedInsteadOfBeingConvertedToTimeout()
    {
        var result = await AvitoInteractionWaiter.WaitAsync(
            (_, _) => Task.FromException<string>(new InvalidOperationException("CDP context lost")),
            AvitoInteractionWaiter.Target.Candidates,
            new AvitoInteractionWaiter.Options(100, 5),
            "https://www.avito.ru/profile/candidates",
            CancellationToken.None);

        Assert.Equal(AvitoInteractionWaiter.Status.ProbeFailed, result.Status);
        Assert.Contains("page_state_probe_failed", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_IsBoundedByElapsedTimeWhenProbeNeverCompletes()
    {
        var never = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var result = await AvitoInteractionWaiter.WaitAsync(
            (_, _) => never.Task,
            AvitoInteractionWaiter.Target.Candidates,
            new AvitoInteractionWaiter.Options(40, 5),
            "https://www.avito.ru/profile/candidates",
            CancellationToken.None);

        Assert.Equal(AvitoInteractionWaiter.Status.TimedOut, result.Status);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 1_000);
    }

    [Fact]
    public async Task ReadyResult_ContainsLastDomSnapshot()
    {
        var result = await AvitoInteractionWaiter.WaitAsync(
            (script, _) => Task.FromResult(script.Contains("const target = \"Candidates\"", StringComparison.Ordinal)
                ? TargetJson(ready: true, reason: "items", readyState: "interactive", itemCount: 1)
                : PageStateJson()),
            AvitoInteractionWaiter.Target.Candidates,
            new AvitoInteractionWaiter.Options(100, 5),
            "https://www.avito.ru/profile/candidates",
            CancellationToken.None);

        Assert.NotNull(result.LastDomSnapshot);
        Assert.Contains("\"ready\":true", result.LastDomSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidOptions_AreRejectedBeforePolling()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AvitoInteractionWaiter.WaitAsync(
            (_, _) => Task.FromResult(PageStateJson()),
            AvitoInteractionWaiter.Target.Candidates,
            new AvitoInteractionWaiter.Options(100, 0),
            "https://www.avito.ru/profile/candidates",
            CancellationToken.None));
    }

    [Fact]
    public async Task BlockedItemsOnWrongTab_AreRejectedBeforeBestEffortCapture()
    {
        var result = await AvitoInteractionWaiter.WaitAsync(
            (script, _) => Task.FromResult(script.Contains("const target = \"BlockedItems\"", StringComparison.Ordinal)
                ? TargetJson(ready: false, reason: "target_not_available", readyState: "interactive", contextMismatch: true)
                : PageStateJson(pageKind: "profileitems")),
            AvitoInteractionWaiter.Target.BlockedItems,
            new AvitoInteractionWaiter.Options(100, 1),
            "https://www.avito.ru/profile/pro/items",
            CancellationToken.None);

        Assert.Equal(AvitoInteractionWaiter.Status.ContextMismatch, result.Status);
    }

    private static string PageStateJson(
        string pageKind = "candidates",
        bool hasLogin = false,
        bool hasCaptcha = false) =>
        $$"""
          {"pageKind":"{{pageKind}}","url":"https://www.avito.ru/profile/candidates","title":"","profileSwitchModalOpen":false,"profileCardsCount":0,"currentSubProfileId":null,"currentSubProfileName":null,"candidatesItemCount":0,"hasLoginForm":{{hasLogin.ToString().ToLowerInvariant()}},"hasCaptcha":{{hasCaptcha.ToString().ToLowerInvariant()}},"hasFirewallIp":false}
          """;

    private static string TargetJson(
        bool ready,
        string reason,
        string readyState,
        bool hasLoader = false,
        int itemCount = 0,
        int statusCount = 0,
        bool contextMismatch = false) =>
        $$"""
          {"ready":{{ready.ToString().ToLowerInvariant()}},"reason":"{{reason}}","url":"https://www.avito.ru/profile/candidates","readyState":"{{readyState}}","hasLoader":{{hasLoader.ToString().ToLowerInvariant()}},"itemCount":{{itemCount}},"statusCount":{{statusCount}},"currentSubProfileId":null,"contextMismatch":{{contextMismatch.ToString().ToLowerInvariant()}}}
          """;
}
