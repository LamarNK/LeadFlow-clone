using System.Diagnostics;
using System.Text.Json;
using LeadFlow.Core.Logging.Audit;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Waits for the smallest DOM contract required by a concrete Avito operation.
/// It deliberately does not use <c>document.readyState === "complete"</c>: Avito
/// keeps non-essential widgets alive after the data needed by the worker is present.
/// </summary>
internal static class AvitoInteractionWaiter
{
    public enum Target
    {
        Candidates,
        ProfileItems,
        BlockedItems,
        ProfileSidebar,
        ProfileBalance,
        LoginForm,
        AuthorizedProfile,
        SubProfileSwitch,
        Messenger
    }

    public enum Status
    {
        Ready,
        TimedOut,
        LoginRequired,
        CaptchaOrFirewall,
        ProbeFailed,
        ContextMismatch
    }

    public sealed record Options(
        int TimeoutMs,
        int PollMs,
        string? ExpectedSubProfileId = null,
        string? Operation = null);

    public sealed record Result(
        Status Status,
        Target Target,
        string Reason,
        long WaitMs,
        string? Url,
        string? ReadyState,
        bool HasLoader,
        int ItemCount,
        int StatusCount,
        string? ExpectedSubProfileId,
        string? CurrentSubProfileId,
        string? LastDomSnapshot,
        AvitoPageState? PageState);

    public static Task<Result> WaitOnPageAsync(
        IPage page,
        Target target,
        Options options,
        CancellationToken cancellationToken = default) =>
        WaitAsync(
            async (script, _) =>
                await page.EvaluateExpressionAsync<string>($"JSON.stringify({script})")
                    .ConfigureAwait(false) ?? string.Empty,
            target,
            options,
            page.Url,
            cancellationToken);

    public static async Task<Result> WaitAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        Target target,
        Options options,
        string? pageUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executeScript);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.TimeoutMs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PollMs, 0);

        var stopwatch = Stopwatch.StartNew();
        InteractionSnapshot? lastSnapshot = null;
        AvitoPageState? lastPageState = null;
        var contextMismatchPolls = 0;
        Func<string, CancellationToken, Task<string>> executeBeforeDeadline = (script, ct) =>
            ExecuteBeforeDeadlineAsync(executeScript, script, stopwatch, options.TimeoutMs, ct);

        while (stopwatch.ElapsedMilliseconds < options.TimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                lastPageState = await AvitoPageStateProbe.TryProbeAsync(executeBeforeDeadline, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException) when (stopwatch.ElapsedMilliseconds >= options.TimeoutMs)
            {
                break;
            }
            catch (Exception ex)
            {
                var failed = CreateResult(
                    Status.ProbeFailed,
                    target,
                    $"page_state_probe_failed:{ex.GetType().Name}",
                    stopwatch,
                    pageUrl,
                    lastSnapshot,
                    lastPageState,
                    options.ExpectedSubProfileId);
                Log(failed, options.Operation, DeskLinkAuditLogLevel.Error);
                return failed;
            }

            if (lastPageState is { HasCaptcha: true })
            {
                var result = CreateResult(Status.CaptchaOrFirewall, target, "captcha_or_firewall", stopwatch, pageUrl, lastSnapshot, lastPageState, options.ExpectedSubProfileId);
                Log(result, options.Operation, DeskLinkAuditLogLevel.Warning);
                return result;
            }

            if (target != Target.LoginForm
                && (lastPageState is { HasLoginForm: true } or { PageKind: AvitoPageKind.Login }))
            {
                var result = CreateResult(Status.LoginRequired, target, "login_required", stopwatch, pageUrl, lastSnapshot, lastPageState, options.ExpectedSubProfileId);
                Log(result, options.Operation, DeskLinkAuditLogLevel.Warning);
                return result;
            }

            try
            {
                lastSnapshot = await TryReadSnapshotAsync(executeBeforeDeadline, target, options.ExpectedSubProfileId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException) when (stopwatch.ElapsedMilliseconds >= options.TimeoutMs)
            {
                break;
            }
            catch (Exception ex)
            {
                var failed = CreateResult(
                    Status.ProbeFailed,
                    target,
                    $"target_probe_failed:{ex.GetType().Name}",
                    stopwatch,
                    pageUrl,
                    lastSnapshot,
                    lastPageState,
                    options.ExpectedSubProfileId);
                Log(failed, options.Operation, DeskLinkAuditLogLevel.Error);
                return failed;
            }
            if (lastSnapshot?.ContextMismatch == true)
            {
                contextMismatchPolls++;
                if (contextMismatchPolls >= 2)
                {
                    var mismatch = CreateResult(
                        Status.ContextMismatch,
                        target,
                        "target_context_mismatch",
                        stopwatch,
                        pageUrl,
                        lastSnapshot,
                        lastPageState,
                        options.ExpectedSubProfileId);
                    Log(mismatch, options.Operation, DeskLinkAuditLogLevel.Warning);
                    return mismatch;
                }
            }
            else
            {
                contextMismatchPolls = 0;
            }
            if (lastSnapshot?.Ready == true)
            {
                var result = CreateResult(Status.Ready, target, lastSnapshot.Reason, stopwatch, pageUrl, lastSnapshot, lastPageState, options.ExpectedSubProfileId);
                Log(result, options.Operation, DeskLinkAuditLogLevel.Info);
                return result;
            }

            var remainingMs = options.TimeoutMs - stopwatch.ElapsedMilliseconds;
            if (remainingMs > 0)
            {
                await Task.Delay((int)Math.Min((long)options.PollMs, remainingMs), cancellationToken).ConfigureAwait(false);
            }
        }

        var timeout = CreateResult(Status.TimedOut, target, "target_not_available", stopwatch, pageUrl, lastSnapshot, lastPageState, options.ExpectedSubProfileId);
        Log(timeout, options.Operation, DeskLinkAuditLogLevel.Warning);
        return timeout;
    }

    private static Result CreateResult(
        Status status,
        Target target,
        string reason,
        Stopwatch stopwatch,
        string? pageUrl,
        InteractionSnapshot? snapshot,
        AvitoPageState? pageState,
        string? expectedSubProfileId) =>
        new(
            status,
            target,
            reason,
            stopwatch.ElapsedMilliseconds,
            snapshot?.Url ?? pageState?.Url ?? pageUrl,
            snapshot?.ReadyState,
            snapshot?.HasLoader == true,
            snapshot?.ItemCount ?? 0,
            snapshot?.StatusCount ?? 0,
            expectedSubProfileId,
            snapshot?.CurrentSubProfileId ?? pageState?.CurrentSubProfileId,
            snapshot?.Raw,
            pageState);

    private static async Task<string> ExecuteBeforeDeadlineAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        string script,
        Stopwatch stopwatch,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var remainingMs = timeoutMs - stopwatch.ElapsedMilliseconds;
        if (remainingMs <= 0)
        {
            throw new TimeoutException("Avito interaction deadline elapsed before DOM probe.");
        }

        return await executeScript(script, cancellationToken)
            .WaitAsync(TimeSpan.FromMilliseconds(remainingMs), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<InteractionSnapshot?> TryReadSnapshotAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        Target target,
        string? expectedSubProfileId,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(BuildProbeScript(target, expectedSubProfileId), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var text = UnwrapJsonString(raw);
            var snapshot = JsonSerializer.Deserialize<InteractionSnapshot>(text, JsonOptions);
            return snapshot is null ? null : snapshot with { Raw = text };
        }
        catch
        {
            return null;
        }
    }

    internal static string BuildProbeScript(Target target, string? expectedSubProfileId = null)
    {
        var expectedId = JsonSerializer.Serialize(expectedSubProfileId?.Trim() ?? string.Empty);
        var targetName = JsonSerializer.Serialize(target.ToString());

        return $$"""
            (() => {
                const target = {{targetName}};
                const expectedSubProfileId = {{expectedId}};
                const readyState = document.readyState || '';
                const url = window.location.href || '';
                const candidatesRoot = document.querySelector("[data-marker='job-applications/list']") ||
                    document.querySelector("[data-marker='filters/status-list-content']");
                const profileRoot = document.querySelector('#personal-items-root-element');
                const targetRoot = target === 'Candidates' ? candidatesRoot :
                    (target === 'ProfileItems' || target === 'BlockedItems' ? profileRoot : document.body);
                const targetText = (targetRoot?.innerText || '').toLowerCase();
                const candidateCount = document.querySelectorAll("[data-marker='job-application/item']").length;
                const statusCount = document.querySelectorAll("[data-marker='job-application/response/status-select-button']").length;
                const profileItemCount = document.querySelectorAll("[data-marker^='item-snippet/']").length;
                const itemCount = target === 'Candidates' ? candidateCount : profileItemCount;
                const hasLoader = !!targetRoot?.querySelector(
                    "[data-marker*='job-application'][data-marker*='loader'], [data-marker='job-applications/loader'], [class*='spinner' i], [class*='loader' i], [class*='styles-loader'], [class*='style-loader']"
                );
                const hasCandidates = candidateCount > 0 || statusCount > 0;
                const candidatesEmpty = candidateCount === 0 && statusCount === 0 && (
                    /нет\s+отклик|откликов\s+нет|пока\s+нет|ничего\s+не\s+найдено/.test(targetText) ||
                    !!candidatesRoot?.querySelector("[data-marker*='empty'], [class*='empty-state' i]")
                );
                const hasItems = profileItemCount > 0;
                const hasItemsEmpty = !!profileRoot?.querySelector("[data-marker='additem'], img[src*='emptystate_personal_items']") ||
                    /активн[а-я]*\s+объявлен[а-я]*\s+нет|нет\s+(активных\s+)?объявлен|у\s+вас\s+нет\s+активных|объявлен[а-я]*\s+не\s+найден|пока\s+пусто|можно\s+создать\s+новое/.test(targetText);
                const rejectedTab = document.querySelector("[data-marker='profile-items-tab/tab(rejected)']");
                const rejectedTabActive = !!rejectedTab && (
                    rejectedTab.getAttribute('aria-selected') === 'true' ||
                    rejectedTab.getAttribute('data-state') === 'active' ||
                    /active|selected|iscurrent/i.test(rejectedTab.className || '')
                );
                let rejectedByUrl = false;
                try {
                    rejectedByUrl = JSON.parse(new URL(url).searchParams.get('filters') || '{}').tabs === 'rejected';
                } catch { }
                const isProfileItemsPage = /\/profile\/pro\/items/i.test(url);
                const rejectedContext = rejectedTab ? rejectedTabActive : rejectedByUrl;
                const contextMismatch = target === 'BlockedItems' && isProfileItemsPage && !rejectedContext;
                const hasBlockedEmpty = rejectedContext && (
                    /объявлен[а-я]*\s+с\s+ошибк|нет\s+объявлен|объявлен[а-я]*\s+нет|пока\s+пусто/.test(targetText) ||
                    !!profileRoot?.querySelector("[data-marker='additem'], img[src*='emptystate_personal_items']")
                );
                const profileSidebar = !!document.querySelector(
                    "[data-marker='header/profile-name'], [data-marker='profile-switch/link'], [data-marker='osp-sidebar/tools/profile/name'], [data-marker='osp-sidebar/tools/profile/avatar'], [data-marker='osp-sidebar/tools/money']"
                );
                const profileBalance = !!document.querySelector("[data-marker='osp-sidebar/tools/money']");
                const isVisible = (el) => !!el && (() => {
                    const style = window.getComputedStyle(el);
                    const rect = el.getBoundingClientRect();
                    return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
                })();
                const loginForm = Array.from(document.querySelectorAll(
                    "[data-marker='login-form/password/input'], form[data-marker='login-form'] input[name='password'], input[name='password'][autocomplete='current-password'], input[type='password']"
                )).some(isVisible);
                const messenger = !!document.querySelector("[data-marker='messagesHistory/list']") ||
                    !!document.querySelector("a[data-marker='mini-messenger/messenger-page-link']") ||
                    /\/profile\/messenger\/channel\//i.test(url);
                const switchModal = !!document.querySelector("[data-marker='component-profile-switch/root']");
                const switchCards = document.querySelectorAll("[data-marker^='component-profile-switch/profile-']");
                const switchCard = expectedSubProfileId
                    ? Array.from(document.querySelectorAll("[data-marker^='component-profile-switch/profile-']"))
                        .find((el) => el.getAttribute('data-marker') === `component-profile-switch/profile-${expectedSubProfileId}`)
                    : null;
                const currentSubProfileId = switchCard && /isCurrent/i.test(switchCard.className || '')
                    ? expectedSubProfileId
                    : null;

                let ready = false;
                let reason = 'target_not_available';
                if (target === 'Candidates') {
                    ready = hasCandidates || (candidatesEmpty && !hasLoader);
                    reason = hasCandidates ? 'items' : (ready ? 'empty' : reason);
                } else if (target === 'ProfileItems') {
                    ready = hasItems || (hasItemsEmpty && !hasLoader);
                    reason = hasItems ? 'items' : (ready ? 'empty' : reason);
                } else if (target === 'BlockedItems') {
                    ready = rejectedContext && (hasItems || (hasBlockedEmpty && !hasLoader));
                    reason = ready ? (hasItems ? 'items' : 'empty') : reason;
                } else if (target === 'ProfileSidebar' || target === 'AuthorizedProfile') {
                    ready = profileSidebar;
                    reason = ready ? 'profile_sidebar' : reason;
                } else if (target === 'ProfileBalance') {
                    ready = profileBalance;
                    reason = ready ? 'profile_balance' : reason;
                } else if (target === 'LoginForm') {
                    ready = loginForm;
                    reason = ready ? 'login_form' : reason;
                } else if (target === 'SubProfileSwitch') {
                    ready = expectedSubProfileId
                        ? !!currentSubProfileId
                        : (switchModal && switchCards.length > 0);
                    reason = currentSubProfileId ? 'active_subprofile' :
                        (ready ? 'switch_modal_cards' : reason);
                } else if (target === 'Messenger') {
                    ready = messenger;
                    reason = ready ? 'messenger' : reason;
                }

                return {
                    ready,
                    reason,
                    url,
                    readyState,
                    hasLoader,
                    itemCount,
                    statusCount,
                    currentSubProfileId,
                    rejectedContext,
                    contextMismatch,
                    profileBalance,
                    messenger
                };
            })()
            """;
    }

    private static void Log(Result result, string? operation, DeskLinkAuditLogLevel level)
    {
        _ = GlobalLogger.Instance.LogAsync(
            $"Avito interaction {result.Status}: {operation ?? result.Target.ToString()} ({result.Reason}).",
            level,
            memberName: nameof(AvitoInteractionWaiter),
            properties: new Dictionary<string, object?>
            {
                ["avito.operation"] = operation ?? result.Target.ToString(),
                ["ready.status"] = result.Status.ToString(),
                ["ready.reason"] = result.Reason,
                ["ready.waitMs"] = result.WaitMs,
                ["readyState"] = result.ReadyState,
                ["hasLoader"] = result.HasLoader,
                ["items.count"] = result.ItemCount,
                ["status.count"] = result.StatusCount,
                ["page.url"] = result.Url,
                ["avito.expectedSubProfileId"] = result.ExpectedSubProfileId,
                ["avito.currentSubProfileId"] = result.CurrentSubProfileId,
                ["ready.domSnapshot"] = result.LastDomSnapshot
            });
    }

    private sealed record InteractionSnapshot(
        bool Ready,
        string Reason,
        string? Url,
        string? ReadyState,
        bool HasLoader,
        int ItemCount,
        int StatusCount,
        string? CurrentSubProfileId,
        bool ContextMismatch = false,
        string? Raw = null);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string UnwrapJsonString(string raw)
    {
        var text = raw.Trim();
        if (text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string>(text) ?? text;
            }
            catch
            {
                return text;
            }
        }

        return text;
    }
}
