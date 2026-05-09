using LeadFlow.Models;

namespace LeadFlow.Services.AdsPower;

/// <summary>Подсказки для CDP-извлечения: пропуск кликов «в чат», если номер уже известен локальной базе.</summary>
public sealed record CandidatesMessengerEnrichmentHints(Guid AccountId, DuplicateScope DuplicateScope);
