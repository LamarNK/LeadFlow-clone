namespace LeadFlow.Models;

public sealed class MonitoringSafetyOptions
{
    /// <summary>Резерв под будущие сценарии; не используется основным циклом мониторинга.</summary>
    public int CheckIntervalSeconds { get; set; } = 90;

    public bool StopOnCaptcha { get; set; } = true;
    public bool StopOnAuthRequired { get; set; } = true;
    public bool AutoStartMonitoring { get; set; }
}
