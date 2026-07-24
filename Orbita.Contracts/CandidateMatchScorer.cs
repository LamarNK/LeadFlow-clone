namespace Orbita.Contracts;

public static class CandidateMatchScorer
{
    public const int MatchScore = 100;
    public const int MatchThreshold = 70;

    /// <summary>
    /// Полное ФИО (Ф+И+О): совпадение имени достаточно (телефон может меняться).
    /// Неполное имя (только имя или Ф+И без отчества): одного имени мало —
    /// нужны доп. параметры. Ф+И — возраст или город; одно слово — возраст и город.
    /// Временное окно (~неделя по дате отклика) применяется в CandidatePersonMatchService.
    /// Совпадение телефона при том же тексте имени по-прежнему даёт максимальный score.
    /// </summary>
    public static int CalculateScore(CandidateMatchProfile existing, CandidateMatchProfile incoming)
    {
        var existingName = CandidateNameNormalizer.Normalize(existing.FullName);
        var incomingName = CandidateNameNormalizer.Normalize(incoming.FullName);
        if (!CandidateNameNormalizer.IsFullNameMatch(existingName, incomingName))
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(existing.PhoneNormalized)
            && !string.IsNullOrWhiteSpace(incoming.PhoneNormalized)
            && string.Equals(existing.PhoneNormalized, incoming.PhoneNormalized, StringComparison.Ordinal))
        {
            return MatchScore;
        }

        var ageMatches = existing.Age.HasValue
            && incoming.Age.HasValue
            && existing.Age.Value == incoming.Age.Value;
        var cityMatches = CityNormalizer.IsMatch(existing.City, incoming.City);

        // Полное ФИО с обеих сторон — имя само по себе = match.
        if (CandidateNameNormalizer.IsCompleteFio(existingName)
            && CandidateNameNormalizer.IsCompleteFio(incomingName))
        {
            var total = MatchThreshold;
            if (ageMatches)
            {
                total += 10;
            }

            if (cityMatches)
            {
                total += 10;
            }

            return total;
        }

        // Неполное имя: без доп. параметров не матчим.
        var tokenCount = incomingName.Tokens.Count;
        var supportingCount = (ageMatches ? 1 : 0) + (cityMatches ? 1 : 0);
        var requiredSupporting = tokenCount <= 1 ? 2 : 1;
        if (supportingCount < requiredSupporting)
        {
            return 0;
        }

        var incompleteTotal = MatchThreshold;
        if (ageMatches)
        {
            incompleteTotal += 10;
        }

        if (cityMatches)
        {
            incompleteTotal += 10;
        }

        return incompleteTotal;
    }

    public static bool IsMatch(CandidateMatchProfile existing, CandidateMatchProfile incoming) =>
        CalculateScore(existing, incoming) >= MatchThreshold;

    public static (T? Best, int Score) TryFindBestMatch<T>(
        IEnumerable<T> candidates,
        CandidateMatchProfile incoming,
        Func<T, CandidateMatchProfile> profileSelector)
    {
        T? best = default;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            var score = CalculateScore(profileSelector(candidate), incoming);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore >= MatchThreshold ? (best, bestScore) : (default, bestScore);
    }
}
