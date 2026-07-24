namespace Orbita.Contracts;

public sealed record NormalizedCandidateName(
    string FullName,
    string LastName,
    string FirstName,
    string MiddleName,
    IReadOnlyList<string> Tokens);

public static class CandidateNameNormalizer
{
    public static NormalizedCandidateName Normalize(string? fullName)
    {
        var tokens = (fullName ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePart)
            .Where(static x => x.Length > 0)
            .ToArray();

        var lastName = tokens.ElementAtOrDefault(0) ?? string.Empty;
        var firstName = tokens.ElementAtOrDefault(1) ?? string.Empty;
        var middleName = tokens.ElementAtOrDefault(2) ?? string.Empty;
        var normalizedFullName = string.Join(' ', tokens);

        return new NormalizedCandidateName(
            normalizedFullName,
            lastName,
            firstName,
            middleName,
            tokens);
    }

    public static bool IsFullNameMatch(NormalizedCandidateName left, NormalizedCandidateName right) =>
        string.Equals(left.FullName, right.FullName, StringComparison.Ordinal);

    /// <summary>
    /// Полное ФИО: фамилия + имя + отчество. Только при нём имя само по себе достаточно для match.
    /// </summary>
    public static bool IsCompleteFio(NormalizedCandidateName name) =>
        name.LastName.Length > 0
        && name.FirstName.Length > 0
        && name.MiddleName.Length > 0;

    /// <summary>Есть хотя бы один токен имени (в т.ч. «только Иван» → LastName).</summary>
    public static bool HasAnyNamePart(NormalizedCandidateName name) =>
        name.LastName.Length > 0;

    private static string NormalizePart(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
