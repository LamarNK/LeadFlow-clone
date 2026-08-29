namespace LeadFlow.Core.Services.LocalChrome;

public static class LocalChromePaths
{
    public const int MaxExecutablePathLength = 512;
    public const int MaxUserDataDirLength = 1024;
    public const int MaxDisplayNameLength = 200;

    public static string ResolveExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var trimmed = configuredPath.Trim();
            if (trimmed.Length > MaxExecutablePathLength)
            {
                throw new InvalidOperationException(
                    $"Путь к Chrome/Chromium не должен превышать {MaxExecutablePathLength} символов.");
            }

            if (!File.Exists(trimmed))
            {
                throw new InvalidOperationException(
                    $"Файл браузера не найден: {trimmed}. Укажите путь к chrome.exe в настройках воркера.");
            }

            return trimmed;
        }

        foreach (var candidate in EnumerateInstalledBrowserPaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Не найден установленный Chrome или Chromium. Укажите путь к chrome.exe в настройках воркера.");
    }

    public static string NormalizeUserDataDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "Укажите путь к отдельной папке профиля Chrome (User Data).");
        }

        var trimmed = path.Trim();
        if (trimmed.Length > MaxUserDataDirLength)
        {
            throw new InvalidOperationException(
                $"Путь к папке профиля не должен превышать {MaxUserDataDirLength} символов.");
        }

        if (IsDefaultBrowserProfile(trimmed))
        {
            throw new InvalidOperationException(
                "Нельзя использовать стандартный профиль Chrome пользователя. Укажите отдельную папку профиля.");
        }

        return trimmed;
    }

    public static void EnsureUserDataDir(string path) => Directory.CreateDirectory(path);

    public static bool IsDefaultBrowserProfile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Trim().Replace('/', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        foreach (var forbidden in EnumerateDefaultProfilePaths())
        {
            if (string.Equals(normalized, forbidden, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static IEnumerable<string> EnumerateInstalledBrowserPaths()
    {
        foreach (var root in EnumerateProgramRoots())
        {
            yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(root, "Google", "Chrome Beta", "Application", "chrome.exe");
            yield return Path.Combine(root, "Chromium", "Application", "chrome.exe");
            yield return Path.Combine(root, "Chromium", "Application", "chromium.exe");
        }
    }

    private static IEnumerable<string> EnumerateProgramRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    private static IEnumerable<string> EnumerateDefaultProfilePaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            yield break;
        }

        var chromeUserData = Path.Combine(local, "Google", "Chrome", "User Data");
        yield return chromeUserData;
        yield return Path.Combine(chromeUserData, "Default");

        var chromiumUserData = Path.Combine(local, "Chromium", "User Data");
        yield return chromiumUserData;
        yield return Path.Combine(chromiumUserData, "Default");
    }
}
