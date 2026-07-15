using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

/// <summary>Человекочитаемые логи проверки дублей при разборе откликов.</summary>
public static class CandidateDedupLog
{
    public static void LogParseDedupSummary(
        AvitoAccount account,
        AvitoSubProfile? subProfile,
        DuplicateScope scope,
        int parsedCount,
        int skippedInBatch)
    {
        if (parsedCount == 0 && skippedInBatch == 0)
        {
            return;
        }

        var who = FormatWho(account, subProfile);
        var parts = new List<string> { $"разобрано {parsedCount}" };
        if (skippedInBatch > 0)
        {
            parts.Add($"повтор sourceResponseId в выборке: {skippedInBatch}");
        }

        LogInfo(
            $"{who} — дедуп при разборе (scope {DescribeScope(scope)}): {string.Join("; ", parts)}.");
    }

    public static void LogPersonProfileLookup(
        Guid accountId,
        int profilesQueried,
        bool apiReached,
        int apiProfileMatches,
        int totalProfileMatches)
    {
        if (profilesQueried == 0)
        {
            return;
        }

        var apiStatus = apiReached
            ? "Orbita API — ответ получен"
            : "Orbita API — недоступен, только локальный кэш телефонов";
        LogInfo(
            $"Дедуп person-match — {apiStatus}; accountId={accountId:D}; " +
            $"профилей {profilesQueried}; API match {apiProfileMatches}; итого пропусков {totalProfileMatches}.");
    }

    public static void LogOrbitaApiLookup(
        Guid accountId,
        DuplicateScope scope,
        string? avitoSubProfileId,
        int sourceIdsQueried,
        int phonesQueried,
        bool apiReached,
        int apiSourceIdsMatched,
        int apiPhonesMatched,
        int cacheSourceIdsMatched,
        int cachePhonesMatched,
        int totalSourceIdsMatched,
        int totalPhonesMatched,
        IReadOnlyCollection<string> matchedPhones)
    {
        if (sourceIdsQueried == 0 && phonesQueried == 0)
        {
            return;
        }

        var apiStatus = apiReached
            ? "Orbita API POST /candidates/lookup — ответ получен"
            : "Orbita API POST /candidates/lookup — недоступен, только локальный кэш воркера";

        var cacheOnlySourceIds = Math.Max(0, totalSourceIdsMatched - apiSourceIdsMatched);
        var cacheOnlyPhones = Math.Max(0, totalPhonesMatched - apiPhonesMatched);

        var details = new List<string>
        {
            $"accountId={accountId:D}",
            $"scope={DescribeScope(scope)}",
            $"subProfile={FormatSubProfileId(avitoSubProfileId)}",
            $"запрос: sourceResponseId {sourceIdsQueried}, телефоны {phonesQueried}"
        };

        if (apiReached)
        {
            details.Add($"API: sourceResponseId {apiSourceIdsMatched}, телефоны {apiPhonesMatched}");
        }

        if (cacheOnlySourceIds > 0 || cacheOnlyPhones > 0)
        {
            details.Add($"кэш воркера (дополнительно): sourceResponseId {cacheOnlySourceIds}, телефоны {cacheOnlyPhones}");
        }

        details.Add($"итого дублей: sourceResponseId {totalSourceIdsMatched}, телефоны {totalPhonesMatched}");

        var phoneSample = FormatPhoneSample(matchedPhones);
        if (!string.IsNullOrEmpty(phoneSample))
        {
            details.Add($"телефоны-дубли: {phoneSample}");
        }

        LogInfo($"Дедуп lookup — {apiStatus}; {string.Join("; ", details)}.");
    }

    public static void LogLocalDbLookup(
        Guid accountId,
        DuplicateScope scope,
        string? avitoSubProfileId,
        int sourceIdsQueried,
        int phonesQueried,
        int sourceIdsMatched,
        int phonesMatched,
        IReadOnlyCollection<string> matchedPhones)
    {
        if (sourceIdsQueried == 0 && phonesQueried == 0)
        {
            return;
        }

        var details = new List<string>
        {
            "источник: локальная БД LeadFlow (SQLite)",
            $"accountId={accountId:D}",
            $"scope={DescribeScope(scope)}",
            $"subProfile={FormatSubProfileId(avitoSubProfileId)}",
            $"запрос: sourceResponseId {sourceIdsQueried}, телефоны {phonesQueried}",
            $"найдено: sourceResponseId {sourceIdsMatched}, телефоны {phonesMatched}"
        };

        var phoneSample = FormatPhoneSample(matchedPhones);
        if (!string.IsNullOrEmpty(phoneSample))
        {
            details.Add($"телефоны-дубли: {phoneSample}");
        }

        LogInfo($"Дедуп lookup — {string.Join("; ", details)}.");
    }

    private static string FormatWho(AvitoAccount account, AvitoSubProfile? subProfile) =>
        subProfile is null
            ? $"«{account.DisplayName}»"
            : $"«{account.DisplayName}» · «{subProfile.Name}»";

    private static string DescribeScope(DuplicateScope scope) => scope switch
    {
        DuplicateScope.PerAvitoAccount => "PerAvitoAccount",
        _ => "GlobalAcrossAllAccounts"
    };

    private static string FormatSubProfileId(string? avitoSubProfileId) =>
        string.IsNullOrWhiteSpace(avitoSubProfileId) ? "—" : avitoSubProfileId.Trim();

    private static string FormatPhoneSample(IReadOnlyCollection<string> phones, int max = 3)
    {
        if (phones.Count == 0)
        {
            return string.Empty;
        }

        var sample = phones
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Take(max)
            .Select(MaskPhone)
            .ToList();
        if (sample.Count == 0)
        {
            return string.Empty;
        }

        return phones.Count > sample.Count
            ? string.Join(", ", sample) + $" и ещё {phones.Count - sample.Count}"
            : string.Join(", ", sample);
    }

    private static string MaskPhone(string phone)
    {
        var digits = phone.Trim();
        if (digits.Length < 6)
        {
            return "***";
        }

        if (digits.Length <= 8)
        {
            return digits[..2] + "***" + digits[^2..];
        }

        return digits[..4] + "***" + digits[^2..];
    }

    private static void LogInfo(string message) =>
        _ = GlobalLogger.Instance.LogAsync(message, DeskLinkAuditLogLevel.Info);
}