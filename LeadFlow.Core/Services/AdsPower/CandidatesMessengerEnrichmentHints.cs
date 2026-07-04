using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.AdsPower;

/// <summary>Подсказки для CDP-извлечения: пропуск кликов «в чат», если номер уже известен локальной базе.</summary>
public sealed record CandidatesMessengerEnrichmentHints(
    Guid AccountId,
    DuplicateScope DuplicateScope,
    string? AvitoSubProfileId = null);
