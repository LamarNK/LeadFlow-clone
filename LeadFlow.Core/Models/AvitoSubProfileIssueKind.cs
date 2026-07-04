namespace LeadFlow.Core.Models;

/// <summary>Тип последней проблемы на суб-профиле Avito Pro во время мониторинга.</summary>
public static class AvitoSubProfileIssueKind
{
    public const string Captcha = "captcha";
    public const string AuthRequired = "auth_required";
    public const string Timeout = "timeout";
    public const string SwitchFailed = "switch_failed";
    public const string RateLimit = "rate_limit";
    public const string DailyLimit = "daily_limit";
    public const string ProfileInUse = "profile_in_use";
    public const string ParseFailed = "parse_failed";
    public const string Other = "other";

    public static string ToDisplayLabel(string? kind) => kind switch
    {
        Captcha => "капча / блок IP",
        AuthRequired => "нужен вход",
        Timeout => "таймаут",
        SwitchFailed => "не переключился",
        RateLimit => "лимит частоты AdsPower",
        DailyLimit => "дневной лимит AdsPower",
        ProfileInUse => "профиль занят",
        ParseFailed => "ошибка парсинга",
        _ => "проблема"
    };
}