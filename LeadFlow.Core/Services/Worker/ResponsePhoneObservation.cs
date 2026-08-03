namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Локальное наблюдение за номером кандидата после первой отправки в Орбиту.
/// Ключ: субпрофиль + нормализованное ФИО.
/// AccountId / SourceResponseId Avito не используются — оба динамические.
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

    /// <summary>
    /// Старт окна наблюдения (момент первой публикации в Орбиту).
    /// После WatchStartedUtc + N часов наблюдение закрывается.
    /// </summary>
    public DateTime? WatchStartedUtc { get; set; }

    /// <summary>
    /// Стабильный SourceResponseId, с которым отклик ушёл/обновляется в Орбите
    /// (phone-watch:… без номера в хеше).
    /// </summary>
    public string PublishedSourceResponseId { get; set; } = string.Empty;

    /// <summary>Окно наблюдения истекло — больше не публикуем смену/старт.</summary>
    public bool ClosedAfterStableSend { get; set; }

    /// <summary>Последний отправленный в Орбиту нормализованный номер (или empty).</summary>
    public string LastPublishedPhoneNormalized { get; set; } = string.Empty;

    /// <summary><see cref="Orbita.Contracts.ResponsePhoneMetricKinds"/> последней публикации.</summary>
    public string LastPublishedMetricKind { get; set; } = string.Empty;
}
