namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Локальное наблюдение за номером кандидата.
/// Ключ: субпрофиль + нормализованное ФИО.
/// AccountId / SourceResponseId не используются — оба динамические на стороне Avito/воркера.
/// </summary>
public sealed class ResponsePhoneObservation
{
    public string AvitoSubProfileId { get; set; } = string.Empty;
    public string FullNameKey { get; set; } = string.Empty;
    public string PhoneRaw { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    /// <summary>Когда впервые увидели текущий номер на этом ключе.</summary>
    public DateTime PhoneFirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    /// <summary>После отправки метрики «не менялся» — больше не трогаем (как FIO-дедуп).</summary>
    public bool ClosedAfterStableSend { get; set; }
    /// <summary>Последний отправленный в Орбиту нормализованный номер (или empty).</summary>
    public string LastPublishedPhoneNormalized { get; set; } = string.Empty;
    /// <summary><see cref="Orbita.Contracts.ResponsePhoneMetricKinds"/> последней публикации.</summary>
    public string LastPublishedMetricKind { get; set; } = string.Empty;
}
