namespace LeadFlow.Core.Services.Captcha;

/// <summary>Тип проверки, фактически отображённый Avito после запуска firewall.</summary>
public enum AvitoCaptchaKind
{
    Unknown = 0,
    GeeTest,
    HCaptcha,
    Internal
}

public sealed record AvitoCaptchaActivation(
    AvitoCaptchaKind Kind,
    bool ContinueClicked,
    string? UserAgent,
    string? SiteKey = null,
    string? ServerKind = null,
    string? ImageData = null)
{
    public static AvitoCaptchaActivation Unknown { get; } = new(AvitoCaptchaKind.Unknown, false, null);

    public bool IsGeeTest => Kind == AvitoCaptchaKind.GeeTest;
    public bool IsHCaptcha => Kind == AvitoCaptchaKind.HCaptcha;
}

public sealed record AvitoVerifyResult(
    int? StatusCode,
    bool Verified,
    string? ResponseBody,
    string? Error)
{
    public static AvitoVerifyResult Empty { get; } = new(null, false, null, null);

    public string? Summary
    {
        get
        {
            var value = !string.IsNullOrWhiteSpace(Error) ? Error : ResponseBody;
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Length <= 400 ? value : value[..400];
        }
    }
}

internal static class AvitoCaptchaKindExtensions
{
    public static AvitoCaptchaKind Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "geetest" => AvitoCaptchaKind.GeeTest,
        "hcaptcha" => AvitoCaptchaKind.HCaptcha,
        "internal" or "internalcaptcha" => AvitoCaptchaKind.Internal,
        _ => AvitoCaptchaKind.Unknown
    };

    public static string ToLogValue(this AvitoCaptchaKind value) => value switch
    {
        AvitoCaptchaKind.GeeTest => "geetest",
        AvitoCaptchaKind.HCaptcha => "hcaptcha",
        AvitoCaptchaKind.Internal => "internal",
        _ => "unknown"
    };
}
