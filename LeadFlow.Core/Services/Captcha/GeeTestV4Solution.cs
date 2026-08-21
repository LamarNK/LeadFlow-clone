namespace LeadFlow.Core.Services.Captcha;

/// <summary>Токены GeeTest v4 из RuCaptcha / 2captcha (API v2).</summary>
public sealed record GeeTestV4Solution(
    string CaptchaId,
    string LotNumber,
    string PassToken,
    string GenTime,
    string CaptchaOutput);

/// <summary>Токен hCaptcha из RuCaptcha / 2captcha API v2.</summary>
public sealed record HCaptchaSolution(string Token);

/// <summary>Текст с картинки внутренней проверки Avito из RuCaptcha API v2.</summary>
public sealed record ImageCaptchaSolution(string Text);
