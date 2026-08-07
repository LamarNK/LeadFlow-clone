namespace Orbita.Contracts;

/// <summary>
/// Политика распределения CRM-лидов (карточек откликов) между менеджерами.
///
/// <para>
/// <b>Владение:</b> менять алгоритм и правила выдачи здесь.
/// CRM workspace (карточки, задачи, UI) не должен дублировать эту логику.
/// </para>
///
/// <para>
/// Текущая политика (v1):
/// <list type="bullet">
/// <item>Новый лид → менеджеры на смене с free capacity; min(load/capacity); tie-break — кто давно не получал.</item>
/// <item>Старт смены → добрать из unassigned FIFO (старые первыми) ровно freeSlots.</item>
/// <item>Нет свободных → лид остаётся без ManagerUserId (очередь).</item>
/// <item>«На смене» — только эффективная смена (<see cref="CrmShiftRules"/>): забытый «Стоп» после MaxDuration не получает лиды.</item>
/// </list>
/// </para>
/// </summary>
public static class CrmLeadDistribution
{
    /// <summary>Снимок менеджера для решения о выдаче (без EF/Identity).</summary>
    public sealed record ManagerCandidate(
        string UserId,
        int Capacity,
        int ActiveLoad,
        DateTime? LastAutoAssignedAtUtc,
        bool IsOnShift);

    /// <summary>Карточка в очереди unassigned.</summary>
    public sealed record QueueCard(
        Guid CardId,
        DateTime CreatedAtUtc);

    /// <summary>Результат выбора менеджера на новый лид.</summary>
    public sealed record AutoAssignDecision(
        string? ManagerUserId,
        string Reason);

    /// <summary>Сколько слотов ещё можно выдать менеджеру.</summary>
    public static int FreeSlots(int capacity, int activeLoad) =>
        Math.Max(0, capacity - Math.Max(0, activeLoad));

    /// <summary>
    /// Выбрать менеджера для нового лида.
    /// Учитываются только <see cref="ManagerCandidate.IsOnShift"/> и free capacity.
    /// </summary>
    public static AutoAssignDecision SelectManagerForNewLead(IEnumerable<ManagerCandidate> managers)
    {
        var free = managers
            .Where(x => x.IsOnShift && x.Capacity > 0 && x.ActiveLoad < x.Capacity)
            .OrderBy(x => (double)x.ActiveLoad / x.Capacity)
            .ThenBy(x => x.LastAutoAssignedAtUtc ?? DateTime.MinValue)
            .ThenBy(x => x.UserId, StringComparer.Ordinal)
            .ToList();

        if (free.Count == 0)
        {
            return new AutoAssignDecision(null, "Нет менеджеров на смене со свободной ёмкостью");
        }

        return new AutoAssignDecision(free[0].UserId, "Авто: min load/capacity");
    }

    /// <summary>
    /// Совместимый API: userId менеджера или null.
    /// </summary>
    public static string? SelectManagerUserId(
        IEnumerable<(string UserId, int Capacity, int Load, DateTime? LastAssignedAtUtc)> managers) =>
        SelectManagerForNewLead(
            managers.Select(x => new ManagerCandidate(
                x.UserId,
                x.Capacity,
                x.Load,
                x.LastAssignedAtUtc,
                IsOnShift: true))).ManagerUserId;

    /// <summary>
    /// Какие карточки из unassigned выдать при старте смены (FIFO по CreatedAtUtc).
    /// </summary>
    public static IReadOnlyList<Guid> SelectCardsForShiftStart(
        IEnumerable<QueueCard> unassignedOldestFirst,
        int freeSlots)
    {
        if (freeSlots <= 0)
        {
            return [];
        }

        return unassignedOldestFirst
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.CardId)
            .Take(freeSlots)
            .Select(x => x.CardId)
            .ToList();
    }
}

/// <summary>Устаревший алиас — используйте <see cref="CrmLeadDistribution"/>.</summary>
[Obsolete("Use CrmLeadDistribution")]
public static class CrmAssignment
{
    public static string? SelectManagerUserId(
        IEnumerable<(string UserId, int Capacity, int Load, DateTime? LastAssignedAtUtc)> managers) =>
        CrmLeadDistribution.SelectManagerUserId(managers);
}
