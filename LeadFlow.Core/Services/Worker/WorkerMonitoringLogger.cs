using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;

namespace LeadFlow.Core.Services.Worker;

/// <summary>Человекочитаемые логи воркера для панели Orbita (сообщение целиком в <see cref="GlobalLogger"/>).</summary>
internal static class WorkerMonitoringLogger
{
    public static void CycleStarted(int accountCount) =>
        LogInfo($"Цикл мониторинга: старт, {accountCount} аккаунт(ов) AdsPower в очереди.");

    public static void CycleParallelism(int parallelism) =>
        LogInfo($"Цикл: параллелизм — до {parallelism} аккаунт(ов) одновременно.");

    public static void CycleFinished(
        double seconds,
        int newResponses,
        int accountsPolled,
        int accountsTotal,
        double nextDelayMinutes) =>
        LogInfo(
            $"Цикл завершён за {seconds:F0} с: новых откликов {newResponses}, опрошено аккаунтов {accountsPolled} из {accountsTotal}, пауза ~{nextDelayMinutes:F0} мин.");

    public static void CycleNotPolledSummary(
        int accountsTotal,
        IReadOnlyList<(string DisplayName, string Reason)> notPolled)
    {
        if (notPolled.Count == 0)
        {
            return;
        }

        var grouped = notPolled
            .GroupBy(static item => item.Reason, StringComparer.Ordinal)
            .Select(static group =>
            {
                var names = string.Join(", ", group.Select(static item => $"«{item.DisplayName}»"));
                return $"{names} — {group.Key}";
            });
        LogWarning(
            $"Не опрошено {notPolled.Count} из {accountsTotal}: {string.Join("; ", grouped)}.");
    }

    public static void CycleSkippedNoAccounts() =>
        LogInfo("Цикл пропущен: нет включённых аккаунтов AdsPower.");

    public static void CycleFailed(string detail) =>
        LogError($"Сбой цикла мониторинга: {detail}");

    public static void AccountStarted(AvitoAccount account, int enabledSubProfiles, int totalSubProfiles) =>
        LogInfo(
            FormatAccount(account) +
            (totalSubProfiles > 0
                ? $" — старт мониторинга, субпрофилей {enabledSubProfiles}/{totalSubProfiles}."
                : " — старт мониторинга, без субпрофилей (один проход)."));

    public static void AccountSkipped(AvitoAccount account, string reason) =>
        LogWarning($"{FormatAccount(account)} — пропущен: {reason}");

    public static void AccountFinished(AvitoAccount account, int newResponses, double seconds, int subProfilesProcessed) =>
        LogInfo(
            $"{FormatAccount(account)} — готово за {seconds:F0} с: новых откликов {newResponses}, обработано субпрофилей {subProfilesProcessed}.");

    public static void AccountFailed(AvitoAccount account, string step, string detail) =>
        LogError($"{FormatAccount(account)} — сбой на шаге «{step}»: {detail}");

    public static void BrowserOpened(AvitoAccount account) =>
        LogInfo($"{FormatAccount(account)} — браузер AdsPower открыт.");

    public static void BrowserClosed(AvitoAccount account) =>
        LogInfo($"{FormatAccount(account)} — браузер AdsPower закрыт.");

    public static void BrowserHousekeepingStarted(string reason, int profileCount) =>
        LogInfo($"Уборка браузеров AdsPower ({reason}): закрываем {profileCount} профиль(ей).");

    public static void BrowserHousekeepingFinished(int closedOk, int profileCount) =>
        LogInfo($"Уборка браузеров AdsPower завершена: stop отправлен для {closedOk} из {profileCount}.");

    public static void BrowserHousekeepingSkipped(string reason) =>
        LogInfo($"Уборка браузеров AdsPower пропущена: {reason}.");

    public static void SubProfileTelemetrySaved(AvitoAccount account, AvitoSubProfile sub) =>
        LogInfo(
            $"{FormatAccount(account)} · «{sub.Name}» — баланс/рейтинг сохранены; snapshot на сайт в течение ~8 с (или сразу при завершении аккаунта).");

    public static void SubProfileStep(
        AvitoAccount account,
        AvitoSubProfile sub,
        int index,
        int total,
        string step) =>
        LogInfo($"{FormatAccount(account)} · «{sub.Name}» ({index}/{total}): {step}");

    public static void SubProfileSwitchFailed(AvitoAccount account, AvitoSubProfile sub, string detail) =>
        LogWarning($"{FormatAccount(account)} · «{sub.Name}» — не переключился: {detail}");

    public static void ExtractionSummary(
        AvitoAccount account,
        AvitoSubProfile? sub,
        AvitoCandidatesExtractionSummary summary)
    {
        var who = sub is null
            ? FormatAccount(account)
            : $"{FormatAccount(account)} · «{sub.Name}»";
        var level = summary.DomItemCount > 0 && summary.ParsedValidCount < summary.DomItemCount
            ? DeskLinkAuditLogLevel.Warning
            : DeskLinkAuditLogLevel.Info;
        Log(level, $"{who} — отклики: {summary.FormatLogLine()}");
    }

    public static void ExtractionPublished(
        AvitoAccount account,
        AvitoSubProfile? sub,
        int published,
        int readyToPublish,
        int cycleLimit,
        int deferredByCycleLimit = 0,
        int skippedPersonDuplicates = 0)
    {
        var who = sub is null
            ? FormatAccount(account)
            : $"{FormatAccount(account)} · «{sub.Name}»";
        var duplicateNote = skippedPersonDuplicates > 0
            ? $" {skippedPersonDuplicates} пропущено (дубль кандидата)."
            : string.Empty;
        var message = deferredByCycleLimit > 0
            ? $"{who} — опубликовано {published} из {readyToPublish} готовых; {deferredByCycleLimit} отложено (лимит {cycleLimit} на субпрофиль за проход).{duplicateNote}"
            : $"{who} — опубликовано {published} из {readyToPublish} готовых (лимит {cycleLimit} на субпрофиль за проход).{duplicateNote}";
        LogInfo(message);
    }

    public static void SubProfileIssue(
        AvitoAccount account,
        AvitoSubProfile sub,
        string kind,
        string detail,
        bool blocking) =>
        LogWarning(
            $"{FormatAccount(account)} · «{sub.Name}» — проблема ({DescribeIssueKind(kind)}): {detail}" +
            (blocking ? " Дальнейший обход аккаунта остановлен." : ""));

    public static void AccountBlockingStop(AvitoAccount account, string reason) =>
        LogWarning($"{FormatAccount(account)} — обход остановлен: {reason}");

    public static void StaleStateCleared(AvitoAccount account, bool blockingStatus, bool errorMessage) =>
        LogInfo(
            $"{FormatAccount(account)} — сброшено устаревшее состояние" +
            (blockingStatus ? " (блокировка)" : "") +
            (errorMessage ? " (сообщение об ошибке)" : "") +
            ", повторный проход.");

    public static void PageStateHint(AvitoAccount account, AvitoSubProfile? sub, AvitoPageState? state)
    {
        if (state is null)
        {
            return;
        }

        var who = sub is null ? FormatAccount(account) : $"{FormatAccount(account)} · «{sub.Name}»";
        LogInfo($"{who} — страница: {state.DescribeForDiagnostics()}");
    }

    private static string FormatAccount(AvitoAccount account) =>
        $"Аккаунт «{account.DisplayName}» (AdsPower {account.AdsPowerProfileId ?? "—"})";

    private static string DescribeIssueKind(string kind) => kind switch
    {
        AvitoSubProfileIssueKind.AuthRequired => "нужен вход",
        AvitoSubProfileIssueKind.Captcha => "капча / IP",
        AvitoSubProfileIssueKind.SwitchFailed => "переключение",
        AvitoSubProfileIssueKind.ParseFailed => "разбор страницы",
        AvitoSubProfileIssueKind.ProfileInUse => "профиль занят",
        _ => kind
    };

    private static void LogInfo(string message) => Log(DeskLinkAuditLogLevel.Info, message);

    private static void LogWarning(string message) => Log(DeskLinkAuditLogLevel.Warning, message);

    private static void LogError(string message) => Log(DeskLinkAuditLogLevel.Error, message);

    private static void Log(DeskLinkAuditLogLevel level, string message) =>
        _ = GlobalLogger.Instance.LogAsync(message, level);
}