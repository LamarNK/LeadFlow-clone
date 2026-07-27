using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

public enum ResponsePhoneWatchAction
{
    /// <summary>Номер сменился — отправить с метрикой PhoneChanged.</summary>
    PublishPhoneChanged,

    /// <summary>Номер стабилен ≥ порога — отправить с метрикой PhoneUnchanged и закрыть.</summary>
    PublishPhoneUnchanged,

    /// <summary>Пропуск: первый проход (только запомнить) / ждём N ч / уже закрыт.</summary>
    Skip
}

public readonly record struct ResponsePhoneWatchDecision(
    ResponsePhoneWatchAction Action,
    string? PreviousPhoneRaw,
    string? PreviousPhoneNormalized,
    int? UnchangedHours,
    DateTime? PhoneChangedAtUtc,
    ResponsePhoneObservation NextObservation);

/// <summary>
/// Решение по отправке отклика на основе сравнения номера между проходами.
/// Идентичность: только субпрофиль + ФИО (AccountId / SourceResponseId — динамические, не используем).
/// В Орбиту уходит ТОЛЬКО при смене номера или когда номер не менялся ≥ N часов.
/// Первый проход — только запомнить, без отправки.
/// </summary>
public static class ResponsePhoneWatchEvaluator
{
    public static string BuildFullNameKey(string? fullName) =>
        CandidateNameNormalizer.Normalize(fullName).FullName;

    public static ResponsePhoneWatchDecision Evaluate(
        ResponsePhoneObservation? existing,
        string avitoSubProfileId,
        string fullNameKey,
        string phoneRaw,
        string phoneNormalized,
        int phoneUnchangedHours,
        DateTime utcNow)
    {
        var sub = (avitoSubProfileId ?? string.Empty).Trim();
        var nameKey = (fullNameKey ?? string.Empty).Trim();
        var phone = (phoneNormalized ?? string.Empty).Trim();
        var raw = (phoneRaw ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(nameKey) || string.IsNullOrWhiteSpace(phone))
        {
            return new ResponsePhoneWatchDecision(
                ResponsePhoneWatchAction.Skip,
                null,
                null,
                null,
                null,
                BuildFresh(sub, nameKey, raw, phone, utcNow));
        }

        // Первый проход: только запомнить номер и отклик, в Орбиту НЕ шлём.
        if (existing is null)
        {
            return new ResponsePhoneWatchDecision(
                ResponsePhoneWatchAction.Skip,
                null,
                null,
                null,
                null,
                BuildFresh(sub, nameKey, raw, phone, utcNow));
        }

        if (existing.ClosedAfterStableSend)
        {
            var closed = Clone(existing);
            closed.LastSeenUtc = utcNow;
            return new ResponsePhoneWatchDecision(
                ResponsePhoneWatchAction.Skip,
                null,
                null,
                null,
                null,
                closed);
        }

        var samePhone = string.Equals(existing.PhoneNormalized, phone, StringComparison.Ordinal);
        if (!samePhone)
        {
            var prevRaw = existing.PhoneRaw;
            var prevNorm = existing.PhoneNormalized;
            var changed = Clone(existing);
            changed.PhoneRaw = raw;
            changed.PhoneNormalized = phone;
            changed.PhoneFirstSeenUtc = utcNow;
            changed.LastSeenUtc = utcNow;
            changed.LastPublishedPhoneNormalized = phone;
            changed.LastPublishedMetricKind = ResponsePhoneMetricKinds.PhoneChanged;
            changed.ClosedAfterStableSend = false;
            return new ResponsePhoneWatchDecision(
                ResponsePhoneWatchAction.PublishPhoneChanged,
                prevRaw,
                prevNorm,
                null,
                utcNow,
                changed);
        }

        // Тот же номер — ждём порог N часов, потом одна отправка «не менялся».
        var watching = Clone(existing);
        watching.LastSeenUtc = utcNow;
        if (string.IsNullOrWhiteSpace(watching.PhoneRaw) && !string.IsNullOrWhiteSpace(raw))
        {
            watching.PhoneRaw = raw;
        }

        var thresholdHours = phoneUnchangedHours;
        if (thresholdHours <= 0)
        {
            // Стабильность выключена — только смена номера.
            return new ResponsePhoneWatchDecision(
                ResponsePhoneWatchAction.Skip,
                null,
                null,
                null,
                null,
                watching);
        }

        if (string.Equals(
                existing.LastPublishedMetricKind,
                ResponsePhoneMetricKinds.PhoneUnchanged,
                StringComparison.Ordinal)
            && string.Equals(existing.LastPublishedPhoneNormalized, phone, StringComparison.Ordinal))
        {
            watching.ClosedAfterStableSend = true;
            return new ResponsePhoneWatchDecision(
                ResponsePhoneWatchAction.Skip,
                null,
                null,
                null,
                null,
                watching);
        }

        var held = utcNow - existing.PhoneFirstSeenUtc;
        if (held < TimeSpan.FromHours(thresholdHours))
        {
            return new ResponsePhoneWatchDecision(
                ResponsePhoneWatchAction.Skip,
                null,
                null,
                null,
                null,
                watching);
        }

        var unchangedHours = (int)Math.Floor(held.TotalHours);
        if (unchangedHours < thresholdHours)
        {
            unchangedHours = thresholdHours;
        }

        watching.LastPublishedPhoneNormalized = phone;
        watching.LastPublishedMetricKind = ResponsePhoneMetricKinds.PhoneUnchanged;
        watching.ClosedAfterStableSend = true;
        return new ResponsePhoneWatchDecision(
            ResponsePhoneWatchAction.PublishPhoneUnchanged,
            null,
            null,
            unchangedHours,
            null,
            watching);
    }

    private static ResponsePhoneObservation BuildFresh(
        string sub,
        string nameKey,
        string raw,
        string phone,
        DateTime utcNow) =>
        new()
        {
            AvitoSubProfileId = sub,
            FullNameKey = nameKey,
            PhoneRaw = raw,
            PhoneNormalized = phone,
            PhoneFirstSeenUtc = utcNow,
            LastSeenUtc = utcNow,
            ClosedAfterStableSend = false,
            // Ещё ничего не публиковали — только наблюдение.
            LastPublishedPhoneNormalized = string.Empty,
            LastPublishedMetricKind = ResponsePhoneMetricKinds.None
        };

    private static ResponsePhoneObservation Clone(ResponsePhoneObservation source) =>
        new()
        {
            AvitoSubProfileId = source.AvitoSubProfileId,
            FullNameKey = source.FullNameKey,
            PhoneRaw = source.PhoneRaw,
            PhoneNormalized = source.PhoneNormalized,
            PhoneFirstSeenUtc = source.PhoneFirstSeenUtc,
            LastSeenUtc = source.LastSeenUtc,
            ClosedAfterStableSend = source.ClosedAfterStableSend,
            LastPublishedPhoneNormalized = source.LastPublishedPhoneNormalized,
            LastPublishedMetricKind = source.LastPublishedMetricKind
        };
}
