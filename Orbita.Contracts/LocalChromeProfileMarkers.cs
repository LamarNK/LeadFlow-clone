namespace Orbita.Contracts;

public static class LocalChromeProfileMarkers
{
    public const string ManagedPrefix = "orbita-managed:";

    public static string CreateManaged(Guid accountId) =>
        ManagedPrefix + accountId.ToString("D");

    public static bool IsManaged(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.TrimStart().StartsWith(ManagedPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryParseAccountId(string? value, out Guid accountId)
    {
        accountId = Guid.Empty;
        if (!IsManaged(value))
        {
            return false;
        }

        var suffix = value!.Trim()[ManagedPrefix.Length..];
        return Guid.TryParse(suffix, out accountId);
    }
}

public static class LocalChromeUserDataRules
{
    public const int MaxUserDataDirLength = 1024;

    public static bool TryNormalizeAttachedDir(string? value, out string normalized, out string? error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Укажите путь к отдельной папке профиля Chrome на машине воркера.";
            return false;
        }

        var trimmed = value.Trim();
        if (LocalChromeProfileMarkers.IsManaged(trimmed))
        {
            error = "Укажите путь к отдельной папке профиля Chrome на машине воркера.";
            return false;
        }

        if (trimmed.Length > MaxUserDataDirLength)
        {
            error = $"Путь к папке профиля не должен превышать {MaxUserDataDirLength} символов.";
            return false;
        }

        if (LooksLikeForbiddenProfile(trimmed))
        {
            error = "Нельзя использовать стандартный профиль Chrome пользователя. Укажите отдельную папку профиля.";
            return false;
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    public static bool LooksLikeForbiddenProfile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('/', '\\').Trim().TrimEnd('\\');
        if (normalized.Length == 0)
        {
            return false;
        }

        var lastSlash = normalized.LastIndexOf('\\');
        var lastSegment = lastSlash >= 0 && lastSlash < normalized.Length - 1
            ? normalized[(lastSlash + 1)..]
            : normalized;
        if (lastSegment.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.EndsWith(@"\Google\Chrome\User Data", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(@"\Google\Chrome\User Data\Default", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(@"\Chromium\User Data", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(@"\Chromium\User Data\Default", StringComparison.OrdinalIgnoreCase);
    }
}
