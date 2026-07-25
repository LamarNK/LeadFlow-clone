using System.Text.Json.Serialization;

namespace Orbita.Contracts;

/// <summary>Фильтры сбора откликов на воркере (пол/возраст + давность отклика).</summary>
public sealed record ResponseCollectionFilters(
    bool Enabled = false,
    bool ExcludeFemale = false,
    bool ExcludeMale = false,
    int? MaxAgeMaleInclusive = null,
    int? MaxAgeFemaleInclusive = null,
    /// <summary>Устаревший единый лимит (JSON/старые клиенты). Если раздельные не заданы — применяется к обоим полам.</summary>
    int? MaxAgeInclusive = null,
    /// <summary>Пропускать отклики старше N дней (по дате отклика из чата Avito). null — без ограничения.</summary>
    int? MaxResponseAgeDays = null)
{
    public static ResponseCollectionFilters Disabled { get; } = new(Enabled: false);

    [JsonIgnore]
    public int? EffectiveMaxAgeMaleInclusive => ClampAge(MaxAgeMaleInclusive ?? MaxAgeInclusive);

    [JsonIgnore]
    public int? EffectiveMaxAgeFemaleInclusive => ClampAge(MaxAgeFemaleInclusive ?? MaxAgeInclusive);

    /// <summary>Максимальный возраст отклика в днях (null — без ограничения).</summary>
    [JsonIgnore]
    public int? EffectiveMaxResponseAgeDays => ClampResponseAgeDays(MaxResponseAgeDays);

    /// <summary>Нормализация и валидация значений из UI/API (без legacy-поля).</summary>
    public static ResponseCollectionFilters Normalize(
        bool enabled,
        bool excludeFemale,
        bool excludeMale = false,
        int? maxAgeMaleInclusive = null,
        int? maxAgeFemaleInclusive = null,
        int? maxResponseAgeDays = null)
    {
        var normalizedMale = ClampAge(maxAgeMaleInclusive);
        var normalizedFemale = ClampAge(maxAgeFemaleInclusive);
        var normalizedResponseAgeDays = ClampResponseAgeDays(maxResponseAgeDays);
        var effectiveEnabled = enabled
            || excludeFemale
            || excludeMale
            || normalizedMale is not null
            || normalizedFemale is not null
            || normalizedResponseAgeDays is not null;

        return new ResponseCollectionFilters(
            effectiveEnabled,
            excludeFemale,
            excludeMale,
            normalizedMale,
            normalizedFemale,
            MaxResponseAgeDays: normalizedResponseAgeDays);
    }

    /// <summary>
    /// Совместимость: старый единый MaxAge → оба пола, если раздельные не заданы.
    /// </summary>
    public static ResponseCollectionFilters NormalizeLegacy(
        bool enabled,
        bool excludeFemale,
        int? maxAgeInclusive,
        bool excludeMale = false,
        int? maxAgeMaleInclusive = null,
        int? maxAgeFemaleInclusive = null,
        int? maxResponseAgeDays = null)
    {
        var male = maxAgeMaleInclusive ?? maxAgeInclusive;
        var female = maxAgeFemaleInclusive ?? maxAgeInclusive;
        return Normalize(enabled, excludeFemale, excludeMale, male, female, maxResponseAgeDays);
    }

    internal static int? ClampAge(int? age)
    {
        if (age is int value && value is >= 1 and <= 120)
        {
            return value;
        }

        return null;
    }

    public static int? ClampResponseAgeDays(int? days)
    {
        if (days is int value && value >= 1)
        {
            return value;
        }

        return null;
    }
}

public static class ResponseCollectionFilterReasons
{
    public const string AgeAboveMax = "age_above_max";
    public const string GenderFemale = "gender_female";
    public const string GenderMale = "gender_male";
    public const string ResponseAgeAboveMaxDays = "response_age_above_max_days";
}

public readonly record struct ResponseCollectionFilterResult(bool Pass, string? RejectReason)
{
    public static ResponseCollectionFilterResult Allowed { get; } = new(true, null);

    public static ResponseCollectionFilterResult Rejected(string reason) => new(false, reason);
}

/// <summary>Единый evaluator фильтров сбора (pre-skip phone + publish + ingest).</summary>
public static class ResponseCollectionFilter
{
    public static ResponseCollectionFilterResult Evaluate(
        int? age,
        string? resolvedGender,
        ResponseCollectionFilters filters)
    {
        if (!filters.Enabled)
        {
            return ResponseCollectionFilterResult.Allowed;
        }

        if (filters.ExcludeFemale
            && string.Equals(resolvedGender, CandidateGenders.Female, StringComparison.Ordinal))
        {
            return ResponseCollectionFilterResult.Rejected(ResponseCollectionFilterReasons.GenderFemale);
        }

        if (filters.ExcludeMale
            && string.Equals(resolvedGender, CandidateGenders.Male, StringComparison.Ordinal))
        {
            return ResponseCollectionFilterResult.Rejected(ResponseCollectionFilterReasons.GenderMale);
        }

        if (age is not int knownAge)
        {
            return ResponseCollectionFilterResult.Allowed;
        }

        if (string.Equals(resolvedGender, CandidateGenders.Male, StringComparison.Ordinal))
        {
            if (filters.EffectiveMaxAgeMaleInclusive is int maxMale && knownAge > maxMale)
            {
                return ResponseCollectionFilterResult.Rejected(ResponseCollectionFilterReasons.AgeAboveMax);
            }

            return ResponseCollectionFilterResult.Allowed;
        }

        if (string.Equals(resolvedGender, CandidateGenders.Female, StringComparison.Ordinal))
        {
            if (filters.EffectiveMaxAgeFemaleInclusive is int maxFemale && knownAge > maxFemale)
            {
                return ResponseCollectionFilterResult.Rejected(ResponseCollectionFilterReasons.AgeAboveMax);
            }

            return ResponseCollectionFilterResult.Allowed;
        }

        // Пол неизвестен: отсекаем по возрасту только если кандидат превысил бы лимит
        // при любом из заданных полов (оба лимита заданы и age выше обоих).
        var maleLimit = filters.EffectiveMaxAgeMaleInclusive;
        var femaleLimit = filters.EffectiveMaxAgeFemaleInclusive;
        if (maleLimit is int m && femaleLimit is int f
            && knownAge > m
            && knownAge > f)
        {
            return ResponseCollectionFilterResult.Rejected(ResponseCollectionFilterReasons.AgeAboveMax);
        }

        return ResponseCollectionFilterResult.Allowed;
    }

    public static ResponseCollectionFilterResult EvaluateCandidate(
        string? fullName,
        int? age,
        string? cardGender,
        string? rawText,
        ResponseCollectionFilters filters)
    {
        if (!filters.Enabled)
        {
            return ResponseCollectionFilterResult.Allowed;
        }

        var resolved = CandidateGenderResolver.Resolve(fullName, cardGender, rawText);
        return Evaluate(age, resolved.Gender, filters);
    }

    /// <summary>Проверить, не старше ли отклик N дней (по дате отклика из чата Avito).</summary>
    public static ResponseCollectionFilterResult EvaluateResponseAge(
        DateTime responseCreatedAt,
        ResponseCollectionFilters filters)
    {
        if (filters.EffectiveMaxResponseAgeDays is not int maxDays)
        {
            return ResponseCollectionFilterResult.Allowed;
        }

        var age = DateTime.UtcNow - responseCreatedAt;
        if (age.TotalDays > maxDays)
        {
            return ResponseCollectionFilterResult.Rejected(ResponseCollectionFilterReasons.ResponseAgeAboveMaxDays);
        }

        return ResponseCollectionFilterResult.Allowed;
    }
}
