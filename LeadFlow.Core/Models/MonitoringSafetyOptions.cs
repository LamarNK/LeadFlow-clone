namespace LeadFlow.Core.Models;

public sealed class MonitoringSafetyOptions
{
    /// <summary>Резерв под будущие сценарии; не используется основным циклом мониторинга.</summary>
    public int CheckIntervalSeconds { get; set; } = 90;

    public bool StopOnCaptcha { get; set; } = true;
    public bool StopOnAuthRequired { get; set; } = true;
    public bool AutoStartMonitoring { get; set; }

    /// <summary>Сколько аккаунтов Авито обрабатывать параллельно в одном цикле мониторинга (1…10).</summary>
    public int MaxConcurrentAccounts { get; set; } = 1;
}
