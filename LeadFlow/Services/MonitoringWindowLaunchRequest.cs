using LeadFlow.Models;

namespace LeadFlow.Services;

/// <summary>Параметры открытия окна мониторинга (фильтр по статусу отклика, сортировка).</summary>
public sealed record MonitoringWindowLaunchRequest(
    ResponseStatus? FocusStatus = null,
    bool SortByStatusAscending = false);
