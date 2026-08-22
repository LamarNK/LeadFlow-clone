using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class AdsPowerAccountGroupFilter
{
    public const string UngroupedValue = "__none__";

    public static string? Normalize(string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        return groupId.Trim();
    }

    public static bool Matches(string? selectedGroupId, string? accountGroupId)
    {
        var selected = Normalize(selectedGroupId);
        if (selected is null)
        {
            return true;
        }

        if (string.Equals(selected, UngroupedValue, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(accountGroupId);
        }

        return string.Equals(selected, accountGroupId?.Trim(), StringComparison.Ordinal);
    }

    public static IReadOnlyList<EventFilterOptionViewModel> BuildOptions(
        IEnumerable<(string? Id, string? Name)> accountGroups,
        IEnumerable<AdsPowerGroupDto>? catalog = null)
    {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in catalog ?? [])
        {
            var id = Normalize(group.GroupId);
            if (id is null)
            {
                continue;
            }

            byId[id] = string.IsNullOrWhiteSpace(group.GroupName) ? id : group.GroupName.Trim();
        }

        var hasUngrouped = false;
        foreach (var (id, name) in accountGroups)
        {
            var normalizedId = Normalize(id);
            if (normalizedId is null)
            {
                hasUngrouped = true;
                continue;
            }

            if (!byId.ContainsKey(normalizedId))
            {
                byId[normalizedId] = string.IsNullOrWhiteSpace(name) ? normalizedId : name.Trim();
            }
        }

        var options = new List<EventFilterOptionViewModel>
        {
            new() { Value = "", Label = "Все группы" }
        };
        if (hasUngrouped)
        {
            options.Add(new() { Value = UngroupedValue, Label = "Без группы" });
        }

        options.AddRange(byId
            .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new EventFilterOptionViewModel { Value = kv.Key, Label = kv.Value }));
        return options;
    }
}
