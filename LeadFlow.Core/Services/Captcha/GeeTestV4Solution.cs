namespace LeadFlow.Core.Services.Captcha;

/// <summary>Токены GeeTest v4 из RuCaptcha / 2captcha (API v2).</summary>
public sealed record GeeTestV4Solution(
    string CaptchaId,
    string LotNumber,
    string PassToken,
    string GenTime,
    string CaptchaOutput,
    RuCaptchaTask? ProviderTask = null);

/// <summary>Токен hCaptcha из RuCaptcha / 2captcha API v2.</summary>
public sealed record HCaptchaSolution(string Token, RuCaptchaTask? ProviderTask = null);

/// <summary>Текст с картинки внутренней проверки Avito из RuCaptcha API v2.</summary>
public sealed record ImageCaptchaSolution(string Text, RuCaptchaTask? ProviderTask = null);

/// <summary>Упорядоченные точки, возвращаемые RuCaptcha для ClickCaptcha.</summary>
public sealed record ClickCaptchaSolution(
    IReadOnlyList<ClickCaptchaPoint> Points,
    RuCaptchaTask? ProviderTask = null);

/// <summary>Координата относительно изображения ClickCaptcha.</summary>
public readonly record struct ClickCaptchaPoint(decimal X, decimal Y);

/// <summary>Идентификатор завершённой задачи RuCaptcha и версия API, создавшая её.</summary>
public sealed record RuCaptchaTask(string Id, RuCaptchaApiVersion ApiVersion);

public enum RuCaptchaApiVersion
{
    V1,
    V2
}
