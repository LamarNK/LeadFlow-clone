using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class WorkerAccountCatalogFilter
{
    public const string AdsPowerProvider = "adspower";
    public const string MultiloginProvider = "multilogin";
    public const string LocalProvider = "local";
    public const string MultiloginFolderPrefix = "mlx:";
    public const string UngroupedValue = AdsPowerAccountGroupFilter.UngroupedValue;

    public static IReadOnlyList<EventFilterOptionViewModel> ProviderOptions { get; } =
    [
        new() { Value = "", Label = "Все источники" },
        new() { Value = AdsPowerProvider, Label = "AdsPower" },
        new() { Value = MultiloginProvider, Label = "Multilogin" },
        new() { Value = LocalProvider, Label = "Обычный браузер" }
    ];

    public static string? NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        var value = provider.Trim();
        if (value.Equals("ads", StringComparison.OrdinalIgnoreCase)
            || value.Equals("adspower", StringComparison.OrdinalIgnoreCase)
            || value.Equals("AdsPower", StringComparison.Ordinal))
        {
            return AdsPowerProvider;
        }

        if (value.Equals("mlx", StringComparison.OrdinalIgnoreCase)
            || value.Equals("multilogin", StringComparison.OrdinalIgnoreCase))
        {
            return MultiloginProvider;
        }

        if (value.Equals("local", StringComparison.OrdinalIgnoreCase)
            || value.Equals("chrome", StringComparison.OrdinalIgnoreCase)
            || value.Equals("localchrome", StringComparison.OrdinalIgnoreCase)
            || value.Contains("обычн", StringComparison.OrdinalIgnoreCase))
        {
            return LocalProvider;
        }

        return null;
    }

    public static bool IsMultilogin(string? multiloginProfileId) =>
        !string.IsNullOrWhiteSpace(multiloginProfileId);

    public static bool IsLocal(string? localUserDataDir, string? multiloginProfileId) =>
        !string.IsNullOrWhiteSpace(localUserDataDir) && string.IsNullOrWhiteSpace(multiloginProfileId);

    public static string ResolveAccountProvider(string? multiloginProfileId, string? localUserDataDir)
    {
        if (IsMultilogin(multiloginProfileId))
        {
            return MultiloginProvider;
        }

        if (IsLocal(localUserDataDir, multiloginProfileId))
        {
            return LocalProvider;
        }

        return AdsPowerProvider;
    }

    public static bool MatchesProvider(string? selectedProvider, string? multiloginProfileId) =>
        MatchesProvider(selectedProvider, multiloginProfileId, localUserDataDir: null);

    public static bool MatchesProvider(
        string? selectedProvider,
        string? multiloginProfileId,
        string? localUserDataDir)
    {
        var selected = NormalizeProvider(selectedProvider);
        if (selected is null)
        {
            return true;
        }

        return selected == ResolveAccountProvider(multiloginProfileId, localUserDataDir);
    }

    public static string? NormalizeLocation(string? groupId) =>
        AdsPowerAccountGroupFilter.Normalize(groupId);

    public static string MultiloginFolderValue(string folderId) =>
        MultiloginFolderPrefix + folderId.Trim();

    public static bool MatchesLocation(
        string? selectedLocation,
        string? adsPowerGroupId,
        string? multiloginFolderId)
    {
        var selected = NormalizeLocation(selectedLocation);
        if (selected is null)
        {
            return true;
        }

        if (string.Equals(selected, UngroupedValue, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(adsPowerGroupId)
                && string.IsNullOrWhiteSpace(multiloginFolderId);
        }

        if (selected.StartsWith(MultiloginFolderPrefix, StringComparison.Ordinal))
        {
            var folderId = selected[MultiloginFolderPrefix.Length..];
            return string.Equals(folderId, multiloginFolderId?.Trim(), StringComparison.Ordinal);
        }

        return AdsPowerAccountGroupFilter.Matches(selected, adsPowerGroupId);
    }

    public static IReadOnlyList<EventFilterOptionViewModel> BuildLocationOptions(
        IEnumerable<WorkerAccountRowViewModel> accounts,
        IEnumerable<AdsPowerGroupDto>? adsPowerCatalog = null)
    {
        var adsGroups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in adsPowerCatalog ?? [])
        {
            var id = AdsPowerAccountGroupFilter.Normalize(group.GroupId);
            if (id is null)
            {
                continue;
            }

            adsGroups[id] = string.IsNullOrWhiteSpace(group.GroupName) ? id : group.GroupName.Trim();
        }

        var mlxFolders = new Dictionary<string, string>(StringComparer.Ordinal);
        var hasUngrouped = false;

        foreach (var account in accounts)
        {
            if (IsMultilogin(account.MultiloginProfileId))
            {
                var folderId = AdsPowerAccountGroupFilter.Normalize(account.MultiloginFolderId);
                if (folderId is null)
                {
                    if (string.IsNullOrWhiteSpace(account.AdsPowerGroupId))
                    {
                        hasUngrouped = true;
                    }

                    continue;
                }

                if (!mlxFolders.ContainsKey(folderId))
                {
                    mlxFolders[folderId] = ShortLabel(folderId);
                }

                continue;
            }

            var adsId = AdsPowerAccountGroupFilter.Normalize(account.AdsPowerGroupId);
            if (adsId is null)
            {
                hasUngrouped = true;
                continue;
            }

            if (!adsGroups.ContainsKey(adsId))
            {
                adsGroups[adsId] = string.IsNullOrWhiteSpace(account.AdsPowerGroupName)
                    ? adsId
                    : account.AdsPowerGroupName.Trim();
            }
        }

        var options = new List<EventFilterOptionViewModel>
        {
            new() { Value = "", Label = "Все группы и папки" }
        };
        if (hasUngrouped)
        {
            options.Add(new() { Value = UngroupedValue, Label = "Без группы" });
        }

        options.AddRange(adsGroups
            .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new EventFilterOptionViewModel
            {
                Value = kv.Key,
                Label = $"AdsPower · {kv.Value}"
            }));
        options.AddRange(mlxFolders
            .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new EventFilterOptionViewModel
            {
                Value = MultiloginFolderValue(kv.Key),
                Label = $"Multilogin · {kv.Value}"
            }));
        return options;
    }

    public static string ShortLabel(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= 12)
        {
            return trimmed;
        }

        return trimmed[..8] + "…";
    }

    public static string ProviderLabel(string? selectedProvider) =>
        NormalizeProvider(selectedProvider) switch
        {
            MultiloginProvider => "Multilogin",
            LocalProvider => "Обычный браузер",
            AdsPowerProvider => "AdsPower",
            _ => "Все источники"
        };
}
