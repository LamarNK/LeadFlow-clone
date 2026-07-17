namespace Orbita.Contracts;

/// <summary>Фильтры сбора откликов на воркере (пол/возраст до и после раскрытия телефона).</summary>
public sealed record ResponseCollectionFilters(
    bool Enabled = false,
    bool ExcludeFemale = false,
    int? MaxAgeInclusive = null)
{
    public static ResponseCollectionFilters Disabled { get; } = new(Enabled: false);

    /// <summary>Нормализация и валидация значений из UI/API.</summary>
    public static ResponseCollectionFilters Normalize(
        bool enabled,
        bool excludeFemale,
        int? maxAgeInclusive)
    {
        int? maxAge = null;
        if (maxAgeInclusive is int age)
        {
            if (age is >= 1 and <= 120)
            {
                maxAge = age;
            }
        }

        return new ResponseCollectionFilters(enabled, excludeFemale, maxAge);
    }
}

public static class ResponseCollectionFilterReasons
{
    public const string AgeAboveMax = "age_above_max";
    public const string GenderFemale = "gender_female";
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

        if (filters.MaxAgeInclusive is int maxAge
            && age is int knownAge
            && knownAge > maxAge)
        {
            return ResponseCollectionFilterResult.Rejected(ResponseCollectionFilterReasons.AgeAboveMax);
        }

        if (filters.ExcludeFemale
            && string.Equals(resolvedGender, CandidateGenders.Female, StringComparison.Ordinal))
        {
            return ResponseCollectionFilterResult.Rejected(ResponseCollectionFilterReasons.GenderFemale);
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
}
