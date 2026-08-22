namespace Orbita.Api.Services;

public static class PanelUserPresenceRules
{
    /// <summary>
    /// Browser heartbeats are sent once per minute. The margin keeps a user online
    /// through a transient failed request without treating a persistent login cookie
    /// as an active session.
    /// </summary>
    public static readonly TimeSpan OnlineThreshold = TimeSpan.FromMinutes(5);

    public static bool IsOnline(DateTime? lastSeenAtUtc, DateTime nowUtc) =>
        lastSeenAtUtc.HasValue
        && lastSeenAtUtc.Value <= nowUtc
        && nowUtc - lastSeenAtUtc.Value <= OnlineThreshold;
}
