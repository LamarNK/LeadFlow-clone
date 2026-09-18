using LeadFlow.Core.Models;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Восстанавливает состояние phone-watch из уже сохранённого в Orbita отклика.
/// Локальный файл воркера остаётся лишь кешем: после его потери мониторинг продолжится
/// по SourceResponseId и времени добавления отклика в Orbita.
/// </summary>
public static class ResponsePhoneWatchOrbitaState
{
    public static string BuildSourceResponseId(CandidateResponse candidate)
    {
        var fullNameKey = ResponsePhoneWatchEvaluator.BuildFullNameKey(candidate?.FullName);
        return string.IsNullOrWhiteSpace(fullNameKey)
            ? string.Empty
            : ResponsePhoneWatchEvaluator.BuildPublishedSourceResponseId(
                candidate?.AvitoSubProfileId,
                fullNameKey);
    }

    public static ResponsePhoneObservation? RestoreObservation(
        WorkerKnownSourceResponseDto? stored,
        string? avitoSubProfileId,
        string? fullNameKey,
        int phoneWatchHours,
        DateTime utcNow)
    {
        if (stored is null
            || !stored.SourceResponseId.StartsWith("phone-watch:", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(fullNameKey))
        {
            return null;
        }

        var watchStartedUtc = stored.CollectedAt == default ? utcNow : stored.CollectedAt;
        var closed = phoneWatchHours <= 0
            || stored.WatchClosedInCrm
            || utcNow - watchStartedUtc >= TimeSpan.FromHours(phoneWatchHours);
        return new ResponsePhoneObservation
        {
            AvitoSubProfileId = (avitoSubProfileId ?? string.Empty).Trim(),
            FullNameKey = fullNameKey.Trim(),
            PhoneRaw = stored.PhoneRaw ?? string.Empty,
            PhoneNormalized = stored.PhoneNormalized ?? string.Empty,
            PhoneFirstSeenUtc = watchStartedUtc,
            LastSeenUtc = utcNow,
            WatchStartedUtc = watchStartedUtc,
            PublishedSourceResponseId = stored.SourceResponseId,
            ClosedAfterStableSend = closed,
            LastPublishedPhoneNormalized = stored.PhoneNormalized ?? string.Empty,
            LastPublishedMetricKind = ResponsePhoneMetricKinds.None
        };
    }

    public static IReadOnlyList<CandidateResponse> OrderByAddedAt(
        IReadOnlyList<CandidateResponse> candidates,
        IEnumerable<WorkerKnownSourceResponseDto> storedResponses)
    {
        var bySourceId = storedResponses
            .Where(static x => !string.IsNullOrWhiteSpace(x.SourceResponseId))
            .GroupBy(static x => x.SourceResponseId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(x => x.CollectedAt).First(),
                StringComparer.OrdinalIgnoreCase);

        return candidates
            .Select((candidate, index) => new
            {
                Candidate = candidate,
                Index = index,
                Stored = bySourceId.GetValueOrDefault(BuildSourceResponseId(candidate))
            })
            .OrderByDescending(static x => x.Stored?.CollectedAt ?? DateTime.MinValue)
            .ThenBy(static x => x.Index)
            .Select(static x => x.Candidate)
            .ToArray();
    }
}
