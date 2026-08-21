namespace LeadFlow.Core.Services.Captcha;

/// <summary>Токены GeeTest v4 из RuCaptcha / 2captcha (API v2).</summary>
public sealed record GeeTestV4Solution(
    string CaptchaId,
    string LotNumber,
    string PassToken,
    string GenTime,
    string CaptchaOutput);
