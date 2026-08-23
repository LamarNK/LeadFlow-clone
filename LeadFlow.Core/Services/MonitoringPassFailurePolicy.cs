using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services;

/// <summary>
/// Что рвёт проход аккаунта, а что только текущий субпрофиль.
/// Капча и отвал прокси — дырки прохода, не 6-часовая блокировка кабинета.
/// </summary>
public static class MonitoringPassFailurePolicy
{
    public const int ConsecutiveCaptchaStopsRemaining = 2;

    public static bool IsAccountBlockingIssueKind(string? kind) =>
        kind is AvitoSubProfileIssueKind.AuthRequired
            or AvitoSubProfileIssueKind.IpBlock;

    public static AvitoAccountStatus? AccountStatusForIssueKind(string? kind) =>
        kind switch
        {
            AvitoSubProfileIssueKind.IpBlock => AvitoAccountStatus.RequiresManualAction,
            AvitoSubProfileIssueKind.AuthRequired => AvitoAccountStatus.RequiresLogin,
            _ => null
        };

    public static bool ShouldStopRemainingAfterCaptcha(int consecutiveCaptchaFails) =>
        consecutiveCaptchaFails >= ConsecutiveCaptchaStopsRemaining;
}
