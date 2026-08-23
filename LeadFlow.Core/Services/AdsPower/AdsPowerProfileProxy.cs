namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Прокси, фактически назначенный профилю AdsPower. Считывается локально на воркере
/// и не передаётся в панель или телеметрию.
/// </summary>
public sealed record AdsPowerProfileProxy(
    string Type,
    string Address,
    string? Username,
    string? Password);
