using LeadFlow.Core.Models;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.AdsPower;

/// <summary>Подсказки для CDP-извлечения: пропуск кликов «в чат», если номер уже известен локальной базе.</summary>
public sealed record CandidatesMessengerEnrichmentHints(
    Guid AccountId,
    DuplicateScope DuplicateScope,
    string? AvitoSubProfileId = null,
    ResponseCollectionFilters? ResponseFilters = null,
    AvitoMessengerAutoReplySettings? MessengerAutoReply = null,
    /// <summary>
    /// true = открытое phone-watch наблюдение (sub+FIO) — нельзя скипать phone-reveal
    /// и messenger-enrich (чат нужно перечитывать, пока следим за номером).
    /// </summary>
    Func<string, CancellationToken, Task<bool>>? IsOpenPhoneWatchAsync = null,
    IReadOnlyDictionary<string, IReadOnlyList<WorkerPendingChatMessageDto>>? PendingBySourceResponseId = null,
    Func<Guid, CancellationToken, Task<bool>>? ClaimOutboundChatForDeliveryAsync = null,
    Func<IReadOnlyList<Guid>, CancellationToken, Task>? AckOutboundChatSentAsync = null,
    /// <summary>Длительность window phone-watch; сохранённые в Orbita записи в этом окне перечитываются.</summary>
    int PhoneWatchHours = ResponsePhoneWatchRules.DefaultUnchangedHours,
    /// <summary>Активные наблюдения Orbita, которые должны быть найдены даже ниже ранней границы скролла.</summary>
    IReadOnlyList<WorkerOpenPhoneWatchDto>? OpenPhoneWatches = null,
    /// <summary>
    /// Разрешает открывать мини-чат, читать историю и отправлять сообщения.
    /// Воркер отключает этот флаг, сохраняя обычный сбор откликов и телефонов.
    /// </summary>
    bool EnableMiniChatActions = true);
