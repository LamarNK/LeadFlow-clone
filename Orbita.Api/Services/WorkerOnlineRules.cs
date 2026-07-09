namespace Orbita.Api.Services;

public static class WorkerOnlineRules
{
    public static readonly TimeSpan OnlineThreshold = TimeSpan.FromMinutes(5);

    public static bool IsOnline(DateTime? lastSeenAtUtc, DateTime nowUtc, bool hubConnected = false) =>
        hubConnected
        || (lastSeenAtUtc.HasValue && nowUtc - lastSeenAtUtc.Value <= OnlineThreshold);
}