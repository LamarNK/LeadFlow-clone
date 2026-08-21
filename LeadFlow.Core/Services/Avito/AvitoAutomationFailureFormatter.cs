using System.Text.Json;
using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

/// <summary>Человекочитаемые сообщения об ошибках автоматизации Avito (для Орбиты).</summary>
public static class AvitoAutomationFailureFormatter
{
    public static string Format(
        string expectedStep,
        AvitoPageState? pageState,
        Exception? inner = null,
        IReadOnlyList<string>? recoveryAttempts = null)
    {
        if (pageState?.HasFirewallIp == true
            || (pageState?.HasCaptcha == true && pageState.PageKind == AvitoPageKind.Captcha))
        {
            return pageState?.HasFirewallIp == true
                ? "доступ ограничен: проблема с IP — откройте браузер AdsPower, дождитесь разблокировки или пройдите проверку."
                : "на странице капча — нужна ручная проверка в браузере AdsPower.";
        }

        if (pageState?.HasCaptcha == true || pageState?.PageKind == AvitoPageKind.Captcha)
        {
            return "на странице капча или блок IP — нужна ручная проверка в браузере.";
        }

        if (SuggestsLogin(pageState))
        {
            return "требуется повторная авторизация в Avito — автовход не удался, откройте браузер AdsPower и войдите (телефон/почта и пароль).";
        }

        if (pageState?.ProfileSwitchModalOpen == true)
        {
            var count = pageState.ProfileCardsCount;
            var suffix = count > 0 ? $" ({count} субпроф.)" : string.Empty;
            var attempts = FormatAttempts(recoveryAttempts);
            return $"не удалось перейти к «{expectedStep}»: открыта модалка «Выбор профиля»{suffix}.{attempts}";
        }

        if (expectedStep.Contains("переключ", StringComparison.OrdinalIgnoreCase)
            && pageState?.IsOnCandidates == true)
        {
            return "не удалось переключить субпрофиль: страница откликов открыта, но переключение не завершилось — возможно, Avito не успел загрузить интерфейс или мешает всплывающее окно (например, «когда вам можно звонить»).";
        }

        if (expectedStep.Contains("переключ", StringComparison.OrdinalIgnoreCase)
            && pageState is not null
            && pageState.PageKind == AvitoPageKind.Unknown)
        {
            return $"не удалось переключить субпрофиль: {pageState.DescribeForDiagnostics()}.";
        }

        if (pageState is not null && !pageState.IsOnCandidates && expectedStep.Contains("отклик", StringComparison.OrdinalIgnoreCase))
        {
            var attempts = FormatAttempts(recoveryAttempts);
            return $"не удалось перейти к откликам: сейчас {pageState.DescribeKindRu()}.{attempts}";
        }

        if (inner is JsonException)
        {
            var context = pageState?.DescribeForDiagnostics() ?? "состояние страницы неизвестно";
            return $"сбой чтения состояния страницы при шаге «{expectedStep}» ({context}).";
        }

        if (!string.IsNullOrWhiteSpace(inner?.Message))
        {
            return inner.Message.Trim();
        }

        return $"ошибка на шаге «{expectedStep}».";
    }

    public static string MapDiagnosticKind(AvitoPageState? pageState, Exception? inner) =>
        pageState switch
        {
            _ when inner is AvitoLoginRequiredException => AvitoSubProfileIssueKind.AuthRequired,
            { HasFirewallIp: true } or { HasCaptcha: true } or { PageKind: AvitoPageKind.Captcha } => AvitoSubProfileIssueKind.Captcha,
            _ when SuggestsLogin(pageState) => AvitoSubProfileIssueKind.AuthRequired,
            { HasLoginForm: true } or { PageKind: AvitoPageKind.Login } => AvitoSubProfileIssueKind.AuthRequired,
            { ProfileSwitchModalOpen: true } => AvitoSubProfileIssueKind.SwitchFailed,
            _ when inner is AvitoPageMismatchException => AvitoSubProfileIssueKind.SwitchFailed,
            _ when inner is JsonException => AvitoSubProfileIssueKind.ParseFailed,
            _ => AvitoSubProfileIssueKind.Other
        };

    public static bool IsAccountBlockingIssue(string kind) =>
        kind is AvitoSubProfileIssueKind.AuthRequired or AvitoSubProfileIssueKind.Captcha;

    private static string FormatAttempts(IReadOnlyList<string>? recoveryAttempts) =>
        recoveryAttempts is { Count: > 0 }
            ? $" Попытки: {string.Join("; ", recoveryAttempts)} — безуспешно."
            : string.Empty;

    public static bool SuggestsLogin(AvitoPageState? pageState)
    {
        if (pageState?.HasLoginForm == true || pageState?.PageKind == AvitoPageKind.Login)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(pageState?.Title)
            && pageState.Title.Contains("Вход", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var url = pageState?.Url;
        return !string.IsNullOrWhiteSpace(url)
               && (url.Contains("/profile/login", StringComparison.OrdinalIgnoreCase)
                   || url.Contains("/profile/auth", StringComparison.OrdinalIgnoreCase)
                   || url.Contains("avito.ru/login", StringComparison.OrdinalIgnoreCase)
                   || url.Contains("#login", StringComparison.OrdinalIgnoreCase));
    }
}