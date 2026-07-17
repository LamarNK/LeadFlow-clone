using System.IO.Compression;
using System.Reflection;

namespace Orbita.Contracts;

/// <summary>
/// Embedded gender lexicon derived from datacoon/russiannames (BSD-3-Clause).
/// Resource: Data/russian-name-gender.tsv.gz — lines <c>kind\ttext\tgender</c> (n|m|s, m|f).
/// </summary>
public static class RussianNameGenderLexicon
{
    private const string ResourceName = "Orbita.Contracts.Data.russian-name-gender.tsv.gz";

    private static readonly Lazy<LexiconMaps> Maps = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int FirstNameCount => Maps.Value.FirstNames.Count;
    public static int MiddleNameCount => Maps.Value.MiddleNames.Count;
    public static int SurnameCount => Maps.Value.Surnames.Count;

    public static string? LookupFirstName(string? token) => Lookup(Maps.Value.FirstNames, token);

    public static string? LookupMiddleName(string? token) => Lookup(Maps.Value.MiddleNames, token);

    public static string? LookupSurname(string? token) => Lookup(Maps.Value.Surnames, token);

    public static bool IsKnownMiddleName(string? token) =>
        !string.IsNullOrEmpty(token) && Maps.Value.MiddleNames.ContainsKey(Normalize(token));

    public static bool IsKnownFirstName(string? token) =>
        !string.IsNullOrEmpty(token) && Maps.Value.FirstNames.ContainsKey(Normalize(token));

    private static string? Lookup(Dictionary<string, string> map, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return map.TryGetValue(Normalize(token), out var gender) ? gender : null;
    }

    internal static string Normalize(string token) => token.Trim().ToLowerInvariant();

    private static LexiconMaps Load()
    {
        var assembly = typeof(RussianNameGenderLexicon).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");

        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        var first = new Dictionary<string, string>(32768, StringComparer.Ordinal);
        var middle = new Dictionary<string, string>(49152, StringComparer.Ordinal);
        var surnames = new Dictionary<string, string>(131072, StringComparer.Ordinal);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var kind = parts[0];
            var text = parts[1];
            var gender = parts[2] switch
            {
                "m" => CandidateGenders.Male,
                "f" => CandidateGenders.Female,
                _ => null
            };
            if (gender is null || text.Length < 2)
            {
                continue;
            }

            var map = kind switch
            {
                "n" => first,
                "m" => middle,
                "s" => surnames,
                _ => null
            };
            map?.TryAdd(text, gender);
        }

        return new LexiconMaps(first, middle, surnames);
    }

    private sealed record LexiconMaps(
        Dictionary<string, string> FirstNames,
        Dictionary<string, string> MiddleNames,
        Dictionary<string, string> Surnames);
}
