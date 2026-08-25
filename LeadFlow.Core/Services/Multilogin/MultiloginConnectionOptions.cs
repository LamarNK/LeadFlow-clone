namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Настройки подключения Multilogin X. Токен не включается в <see cref="ToString"/>.
/// </summary>
public sealed class MultiloginConnectionOptions
{
    public string? LauncherUrl { get; init; }

    public string? CloudApiUrl { get; init; }

    public string? AutomationToken { get; init; }

    public bool HasAutomationToken => !string.IsNullOrWhiteSpace(AutomationToken);

    public MultiloginConnectionOptions Normalized() => new()
    {
        LauncherUrl = MultiloginUrl.Normalize(LauncherUrl),
        CloudApiUrl = MultiloginUrl.Normalize(CloudApiUrl),
        AutomationToken = string.IsNullOrWhiteSpace(AutomationToken) ? null : AutomationToken.Trim()
    };

    public override string ToString() =>
        $"MultiloginConnectionOptions {{ LauncherUrl = {LauncherUrl}, CloudApiUrl = {CloudApiUrl}, HasAutomationToken = {HasAutomationToken} }}";
}
