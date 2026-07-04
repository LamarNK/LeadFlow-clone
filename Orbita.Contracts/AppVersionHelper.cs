using System.Text.RegularExpressions;

namespace Orbita.Contracts;

public static partial class AppVersionHelper
{
    private static readonly Regex ReleaseFileNameRegex = ReleaseFileNamePattern();

    public static bool TryParseFourPart(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Version.TryParse(value.Trim(), out version);
    }

    public static bool IsNewer(string? latest, string? current)
    {
        if (!TryParseFourPart(latest, out var latestVersion))
        {
            return false;
        }

        if (!TryParseFourPart(current, out var currentVersion))
        {
            return true;
        }

        return latestVersion > currentVersion;
    }

    public static bool TryParseVersionFromFileName(string? fileName, out string version)
    {
        version = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var match = ReleaseFileNameRegex.Match(Path.GetFileName(fileName));
        if (!match.Success)
        {
            return false;
        }

        version = match.Groups["version"].Value;
        return TryParseFourPart(version, out _);
    }

    public static string GetReleaseDirectoryName(string version) => $"v{version}";

    [GeneratedRegex(@"^Orbita\.Worker\.Setup-(?<version>\d+\.\d+\.\d+\.\d+)\.msi$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseFileNamePattern();
}