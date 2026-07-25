using LeadFlow.Core.Models;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.AdsPower;

/// <summary>Подсказки для CDP-извлечения: пропуск кликов «в чат», если номер уже известен локальной базе.</summary>
public sealed record CandidatesMessengerEnrichmentHints(
    Guid AccountId,
    DuplicateScope DuplicateScope,
    string? AvitoSubProfileId = null,
    ResponseCollectionFilters? ResponseFilters = null,
    AvitoMessengerAutoReplySettings? MessengerAutoReply = null);
