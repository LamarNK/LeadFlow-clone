namespace Orbita.Contracts;

public static class WorkerHubMethods
{
    public const string Register = nameof(Register);
    public const string Heartbeat = nameof(Heartbeat);
    public const string AckCommand = nameof(AckCommand);
}

public static class WorkerHubEvents
{
    public const string ExecuteCommand = nameof(ExecuteCommand);
    public const string ConfigChanged = nameof(ConfigChanged);
    public const string CaptchaSession = nameof(CaptchaSession);
    public const string BrowserMonitorSession = nameof(BrowserMonitorSession);
    public const string LocalChromeLoginSession = nameof(LocalChromeLoginSession);
    public const string TopUpSession = nameof(TopUpSession);
}

public sealed record WorkerHubRegisterRequest(
    string AppVersion,
    string Status,
    string? StatusDetail);

public sealed record WorkerPushCommandMessage(string Command);

public sealed record WorkerConfigChangedMessage(DateTime OccurredAtUtc);