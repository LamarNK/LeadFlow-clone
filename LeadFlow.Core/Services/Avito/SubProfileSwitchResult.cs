using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

public enum SubProfileSwitchStatus
{
    Ok,
    Captcha,
    IpBlock,
    Login,
    ModalNotReady,
    ClickFailed,
    Unknown
}

public readonly record struct SubProfileSwitchResult(SubProfileSwitchStatus Status, string? Detail = null)
{
    public static SubProfileSwitchResult Succeeded { get; } = new(SubProfileSwitchStatus.Ok);

    public bool Ok => Status == SubProfileSwitchStatus.Ok;

    public bool ShouldDeferRetry =>
        Status is SubProfileSwitchStatus.ModalNotReady
            or SubProfileSwitchStatus.ClickFailed
            or SubProfileSwitchStatus.Unknown;

    public bool IsCaptcha =>
        Status is SubProfileSwitchStatus.Captcha or SubProfileSwitchStatus.IpBlock;

    public bool IsLogin => Status == SubProfileSwitchStatus.Login;

    public bool IsIpBlock => Status == SubProfileSwitchStatus.IpBlock;

    public string JournalErrorType => Status switch
    {
        SubProfileSwitchStatus.Ok => string.Empty,
        SubProfileSwitchStatus.Captcha => "captcha",
        SubProfileSwitchStatus.IpBlock => "ip-block",
        SubProfileSwitchStatus.Login => "auth-required",
        _ => "switch-failed"
    };

    public string JournalMessage(bool deferredRetry) =>
        Status switch
        {
            SubProfileSwitchStatus.Captcha => "капча",
            SubProfileSwitchStatus.IpBlock => "блок IP",
            SubProfileSwitchStatus.Login => "нужен вход",
            SubProfileSwitchStatus.ModalNotReady => deferredRetry
                ? "модалка выбора профиля не готова"
                : "модалка выбора профиля не готова (повтор в этом проходе)",
            SubProfileSwitchStatus.ClickFailed => deferredRetry
                ? "клик по карточке субпрофиля не завершился"
                : "клик по карточке субпрофиля не завершился (повтор в этом проходе)",
            _ => deferredRetry
                ? "не удалось переключить субпрофиль"
                : "не удалось переключить субпрофиль (повтор в этом проходе)"
        };

    public static SubProfileSwitchStatus Classify(AvitoPageState? pageState, string? step)
    {
        if (pageState?.HasFirewallIp == true)
        {
            return SubProfileSwitchStatus.IpBlock;
        }

        if (pageState?.HasCaptcha == true || pageState?.PageKind == AvitoPageKind.Captcha)
        {
            return SubProfileSwitchStatus.Captcha;
        }

        if (pageState?.HasLoginForm == true || pageState?.PageKind == AvitoPageKind.Login)
        {
            return SubProfileSwitchStatus.Login;
        }

        if (string.Equals(step, "switch_modal_not_ready", StringComparison.Ordinal)
            || string.Equals(step, "modal_not_ready", StringComparison.Ordinal))
        {
            return SubProfileSwitchStatus.ModalNotReady;
        }

        if (string.Equals(step, "click_failed", StringComparison.Ordinal)
            || string.Equals(step, "verify_timeout", StringComparison.Ordinal))
        {
            return SubProfileSwitchStatus.ClickFailed;
        }

        return SubProfileSwitchStatus.Unknown;
    }

    public static bool ShouldDeferRetryStatus(SubProfileSwitchStatus status) =>
        status is SubProfileSwitchStatus.ModalNotReady
            or SubProfileSwitchStatus.ClickFailed
            or SubProfileSwitchStatus.Unknown;
}
