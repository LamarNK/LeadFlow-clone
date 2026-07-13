namespace Orbita.Contracts;

public static class CandidateMatchScorer
{
    public const int MatchScore = 100;
    public const int MatchThreshold = 70;

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

        var total = 0;

        if (existing.Age.HasValue
            && incoming.Age.HasValue
            && existing.Age.Value == incoming.Age.Value)
        {
            total += 40;
        }

        if (CityNormalizer.IsMatch(existing.City, incoming.City))
        {
            total += 30;
        }

        return total;
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
