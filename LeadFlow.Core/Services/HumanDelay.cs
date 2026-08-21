namespace LeadFlow.Core.Services;

/// <summary>
/// Рандомные «человеческие» паузы между сайт-операциями, чтобы не палить ботскую частоту запросов.
/// Используем потокобезопасный <see cref="Random.Shared"/>; диапазоны заданы в <see cref="MonitoringTiming"/>.
/// </summary>
public static class HumanDelay
{
    /// <summary>Спит [<paramref name="minMs"/>..<paramref name="maxMs"/>] миллисекунд, равномерное распределение.</summary>
    public static Task DelayAsync(int minMs, int maxMs, CancellationToken cancellationToken = default)
    {
        var lo = Math.Max(0, Math.Min(minMs, maxMs));
        var hi = Math.Max(lo, Math.Max(minMs, maxMs));
        if (hi == 0)
        {
            return Task.CompletedTask;
        }

        // Random.Shared.Next(min, maxExclusive); добавляем +1, чтобы границы включались.
        var ms = lo == hi ? lo : Random.Shared.Next(lo, hi + 1);
        return Task.Delay(ms, cancellationToken);
    }

    public static Task DelaySecondsAsync(int minSeconds, int maxSeconds, CancellationToken cancellationToken = default) =>
        DelayAsync(minSeconds * 1000, maxSeconds * 1000, cancellationToken);

    public static Task BetweenResponsesAsync(CancellationToken cancellationToken = default) =>
        DelaySecondsAsync(
            MonitoringTiming.HumanDelayBetweenResponsesMinSeconds,
            MonitoringTiming.HumanDelayBetweenResponsesMaxSeconds,
            cancellationToken);

    public static Task AfterProfileSwitchAsync(CancellationToken cancellationToken = default) =>
        DelayAsync(
            MonitoringTiming.HumanDelayAfterProfileSwitchMinMs,
            MonitoringTiming.HumanDelayAfterProfileSwitchMaxMs,
            cancellationToken);

    public static Task AfterItemsRenderAsync(CancellationToken cancellationToken = default) =>
        DelayAsync(
            MonitoringTiming.HumanDelayAfterItemsRenderMinMs,
            MonitoringTiming.HumanDelayAfterItemsRenderMaxMs,
            cancellationToken);

    public static Task AfterSwitchModalAsync(CancellationToken cancellationToken = default) =>
        DelayAsync(
            MonitoringTiming.HumanDelayAfterSwitchModalMinMs,
            MonitoringTiming.HumanDelayAfterSwitchModalMaxMs,
            cancellationToken);

    public static Task BetweenSubProfilesAsync(CancellationToken cancellationToken = default) =>
        DelayAsync(
            MonitoringTiming.HumanDelayBetweenSubProfilesMinMs,
            MonitoringTiming.HumanDelayBetweenSubProfilesMaxMs,
            cancellationToken);

    public static Task BeforeCandidateClickAsync(CancellationToken cancellationToken = default) =>
        DelayAsync(
            MonitoringTiming.HumanDelayBeforeCandidateClickMinMs,
            MonitoringTiming.HumanDelayBeforeCandidateClickMaxMs,
            cancellationToken);

    public static Task AfterCandidateClickAsync(CancellationToken cancellationToken = default) =>
        DelayAsync(
            MonitoringTiming.HumanDelayAfterCandidateClickMinMs,
            MonitoringTiming.HumanDelayAfterCandidateClickMaxMs,
            cancellationToken);

    public static Task AfterDetailPanelReadAsync(CancellationToken cancellationToken = default) =>
        DelayAsync(
            MonitoringTiming.HumanDelayAfterDetailPanelReadMinMs,
            MonitoringTiming.HumanDelayAfterDetailPanelReadMaxMs,
            cancellationToken);

    public static Task AfterListScrollAsync(CancellationToken cancellationToken = default) =>
        DelayAsync(
            MonitoringTiming.HumanDelayAfterListScrollMinMs,
            MonitoringTiming.HumanDelayAfterListScrollMaxMs,
            cancellationToken);

    public static Task AfterPhoneRevealClickAsync(CancellationToken cancellationToken = default) =>
        DelayAsync(
            MonitoringTiming.HumanDelayAfterPhoneRevealClickMinMs,
            MonitoringTiming.HumanDelayAfterPhoneRevealClickMaxMs,
            cancellationToken);

    public static Task AfterPhoneRevealOutcomeAsync(bool revealed, CancellationToken cancellationToken = default) =>
        revealed
            ? DelayAsync(
                MonitoringTiming.HumanDelayAfterPhoneRevealSuccessMinMs,
                MonitoringTiming.HumanDelayAfterPhoneRevealSuccessMaxMs,
                cancellationToken)
            : DelayAsync(
                MonitoringTiming.HumanDelayAfterPhoneRevealMissMinMs,
                MonitoringTiming.HumanDelayAfterPhoneRevealMissMaxMs,
                cancellationToken);

    public static Task AfterMessengerCardAsync(CancellationToken cancellationToken = default) =>
        DelayAsync(
            MonitoringTiming.HumanDelayAfterMessengerCardMinMs,
            MonitoringTiming.HumanDelayAfterMessengerCardMaxMs,
            cancellationToken);

    public static Task BeforeMessengerAutoReplySendAsync(CancellationToken cancellationToken = default) =>
        DelayAsync(
            MonitoringTiming.MessengerAutoReplyAfterTypeMinMs,
            MonitoringTiming.MessengerAutoReplyAfterTypeMaxMs,
            cancellationToken);

    /// <summary>Задержка между символами для <c>TypeAsync</c>.</summary>
    public static int NextTypeCharDelayMs()
    {
        var lo = Math.Min(
            MonitoringTiming.HumanTypeCharDelayMinMs,
            MonitoringTiming.HumanTypeCharDelayMaxMs);
        var hi = Math.Max(
            MonitoringTiming.HumanTypeCharDelayMinMs,
            MonitoringTiming.HumanTypeCharDelayMaxMs);
        return lo == hi ? lo : Random.Shared.Next(lo, hi + 1);
    }
}
