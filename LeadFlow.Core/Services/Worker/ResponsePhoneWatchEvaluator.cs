using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

public enum ResponsePhoneWatchAction
{
    /// <summary>Первое появление номера — сразу отправить в Орбиту и открыть окно наблюдения.</summary>
    PublishInitial,

    /// <summary>Номер сменился в окне наблюдения — обновить тот же отклик (метрика PhoneChanged).</summary>
    PublishPhoneChanged,

    /// <summary>Пропуск: тот же номер / ждём / окно закрыто / нет данных.</summary>
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
/// Решение по отправке/обновлению отклика на основе номера между проходами.
/// Идентичность: субпрофиль + ФИО.
/// При первом появлении номера — сразу в Орбиту; N часов (default 5 суток) следим за сменой
/// и дописываем историю в тот же отклик; затем наблюдение закрывается.
/// </summary>
public static class ResponsePhoneWatchEvaluator
{
    public static string BuildFullNameKey(string? fullName) =>
        CandidateNameNormalizer.Normalize(fullName).FullName;

    /// <summary>Стабильный SourceResponseId без номера: один отклик на ключ наблюдения.</summary>
    public static string BuildPublishedSourceResponseId(string? avitoSubProfileId, string? fullNameKey)
    {
        var sub = (avitoSubProfileId ?? string.Empty).Trim();
        var nameKey = (fullNameKey ?? string.Empty).Trim();
        var payload = string.Join('\u001f', sub, nameKey);
        uint hash = 2166136261;
        foreach (var ch in payload)
        {
            hash ^= ch;
            hash *= 16777619;
        }

        return $"phone-watch:{hash:x8}";
    }

    public static ResponsePhoneWatchDecision Evaluate(
        ResponsePhoneObservation? existing,
        string avitoSubProfileId,
        string fullNameKey,
        string phoneRaw,
        string phoneNormalized,
        int phoneWatchHours,
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
                BuildFresh(sub, nameKey, raw, phone, utcNow, published: false));
        }

        // Первый проход: сразу в Орбиту, открываем окно наблюдения.
        if (existing is null)
        {
            var fresh = BuildFresh(sub, nameKey, raw, phone, utcNow, published: true);
            if (phoneWatchHours <= 0)
            {
                fresh.ClosedAfterStableSend = true;
            }

            return new ResponsePhoneWatchDecision(
                ResponsePhoneWatchAction.PublishInitial,
                null,
                null,
                null,
                null,
                fresh);
        }

        // Старые наблюдения без PublishedSourceResponseId — один раз «допубликуем» как initial.
        if (string.IsNullOrWhiteSpace(existing.PublishedSourceResponseId)
            && !existing.ClosedAfterStableSend)
        {
            var migrate = Clone(existing);
            migrate.PhoneRaw = string.IsNullOrWhiteSpace(raw) ? migrate.PhoneRaw : raw;
            migrate.PhoneNormalized = phone;
            migrate.LastSeenUtc = utcNow;
            migrate.PhoneFirstSeenUtc = utcNow;
            migrate.WatchStartedUtc = utcNow;
            migrate.PublishedSourceResponseId = BuildPublishedSourceResponseId(sub, nameKey);
            migrate.LastPublishedPhoneNormalized = phone;
            migrate.LastPublishedMetricKind = ResponsePhoneMetricKinds.None;
            migrate.ClosedAfterStableSend = phoneWatchHours <= 0;
            return new ResponsePhoneWatchDecision(
                ResponsePhoneWatchAction.PublishInitial,
                null,
                null,
                null,
                null,
                migrate);
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

        var watching = Clone(existing);
        watching.LastSeenUtc = utcNow;
        if (string.IsNullOrWhiteSpace(watching.PhoneRaw) && !string.IsNullOrWhiteSpace(raw))
        {
            watching.PhoneRaw = raw;
        }

        var watchStarted = existing.WatchStartedUtc ?? existing.PhoneFirstSeenUtc;
        if (phoneWatchHours > 0 && utcNow - watchStarted >= TimeSpan.FromHours(phoneWatchHours))
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

        var samePhone = string.Equals(existing.PhoneNormalized, phone, StringComparison.Ordinal)
            || string.Equals(existing.LastPublishedPhoneNormalized, phone, StringComparison.Ordinal);
        if (samePhone)
        {
            watching.PhoneNormalized = phone;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                watching.PhoneRaw = raw;
            }

            return new ResponsePhoneWatchDecision(
                ResponsePhoneWatchAction.Skip,
                null,
                null,
                null,
                null,
                watching);
        }

        // Номер сменился в окне — публикуем обновление того же отклика.
        var prevRaw = existing.PhoneRaw;
        var prevNorm = string.IsNullOrWhiteSpace(existing.LastPublishedPhoneNormalized)
            ? existing.PhoneNormalized
            : existing.LastPublishedPhoneNormalized;
        watching.PhoneRaw = raw;
        watching.PhoneNormalized = phone;
        watching.PhoneFirstSeenUtc = utcNow;
        watching.LastPublishedPhoneNormalized = phone;
        watching.LastPublishedMetricKind = ResponsePhoneMetricKinds.PhoneChanged;
        if (string.IsNullOrWhiteSpace(watching.PublishedSourceResponseId))
        {
            watching.PublishedSourceResponseId = BuildPublishedSourceResponseId(sub, nameKey);
        }

        if (watching.WatchStartedUtc is null)
        {
            watching.WatchStartedUtc = watchStarted;
        }

        return new ResponsePhoneWatchDecision(
            ResponsePhoneWatchAction.PublishPhoneChanged,
            prevRaw,
            prevNorm,
            null,
            utcNow,
            watching);
    }

    private static ResponsePhoneObservation BuildFresh(
        string sub,
        string nameKey,
        string raw,
        string phone,
        DateTime utcNow,
        bool published) =>
        new()
        {
            AvitoSubProfileId = sub,
            FullNameKey = nameKey,
            PhoneRaw = raw,
            PhoneNormalized = phone,
            PhoneFirstSeenUtc = utcNow,
            LastSeenUtc = utcNow,
            WatchStartedUtc = published ? utcNow : null,
            PublishedSourceResponseId = published
                ? BuildPublishedSourceResponseId(sub, nameKey)
                : string.Empty,
            ClosedAfterStableSend = false,
            LastPublishedPhoneNormalized = published ? phone : string.Empty,
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
            WatchStartedUtc = source.WatchStartedUtc,
            PublishedSourceResponseId = source.PublishedSourceResponseId,
            ClosedAfterStableSend = source.ClosedAfterStableSend,
            LastPublishedPhoneNormalized = source.LastPublishedPhoneNormalized,
            LastPublishedMetricKind = source.LastPublishedMetricKind
        };
}
