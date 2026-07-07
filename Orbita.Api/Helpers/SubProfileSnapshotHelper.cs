using Orbita.Contracts;

namespace Orbita.Api.Helpers;

internal static class SubProfileSnapshotHelper
{
    private static readonly string[] PlaceholderTokens = ["—", "-", "–", "n/a", "субпрофиль"];

    public static bool IsPlaceholderToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return PlaceholderTokens.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public static bool HasIdentifiableSubProfile(WorkerSubProfileDto profile)
    {
        var id = profile.Id?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(id) && !IsPlaceholderToken(id))
        {
            return true;
        }

        var name = profile.Name?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(name) && !IsPlaceholderToken(name);
    }

    public static bool HasMeaningfulSubProfileList(IReadOnlyList<WorkerSubProfileDto>? profiles) =>
        profiles is { Count: > 0 } && profiles.Any(HasIdentifiableSubProfile);

    public static int CountIdentifiableSubProfiles(IReadOnlyList<WorkerSubProfileDto>? profiles) =>
        profiles?.Count(HasIdentifiableSubProfile) ?? 0;

    public static bool ShouldPersistSubProfiles(
        IReadOnlyList<WorkerSubProfileDto> incoming,
        string? existingJson)
    {
        if (!HasMeaningfulSubProfileList(incoming))
        {
            return false;
        }

        var existing = SubProfileDeserializer.Deserialize(existingJson);
        if (!HasMeaningfulSubProfileList(existing))
        {
            return true;
        }

        if (incoming.All(static p => !p.Balance.HasValue && !p.WalletBalance.HasValue)
            && existing!.Any(static p => p.Balance.HasValue || p.WalletBalance.HasValue))
        {
            return false;
        }

        if (CountIdentifiableSubProfiles(incoming) < CountIdentifiableSubProfiles(existing))
        {
            return false;
        }

        return true;
    }

    public static IReadOnlyList<WorkerSubProfileDto>? MergeForPersist(
        IReadOnlyList<WorkerSubProfileDto>? incoming,
        string? existingJson)
    {
        if (incoming is null || incoming.Count == 0)
        {
            return null;
        }

        var meaningfulIncoming = incoming.Where(HasIdentifiableSubProfile).ToList();
        if (meaningfulIncoming.Count == 0)
        {
            return null;
        }

        if (!ShouldPersistSubProfiles(meaningfulIncoming, existingJson))
        {
            return null;
        }

        var existing = SubProfileDeserializer.Deserialize(existingJson);
        if (!HasMeaningfulSubProfileList(existing))
        {
            return meaningfulIncoming;
        }

        return MergeProfiles(meaningfulIncoming, existing!);
    }

    private static IReadOnlyList<WorkerSubProfileDto> MergeProfiles(
        IReadOnlyList<WorkerSubProfileDto> incoming,
        IReadOnlyList<WorkerSubProfileDto> existing)
    {
        var existingById = existing
            .Where(static p => !string.IsNullOrWhiteSpace(p.Id))
            .ToDictionary(static p => p.Id.Trim(), static p => p, StringComparer.Ordinal);

        return incoming
            .Select(profile =>
            {
                var id = profile.Id.Trim();
                if (!existingById.TryGetValue(id, out var stored))
                {
                    return profile;
                }

                return profile with
                {
                    Balance = profile.Balance ?? stored.Balance,
                    WalletBalance = profile.WalletBalance ?? stored.WalletBalance,
                    AdvanceDurationText = string.IsNullOrWhiteSpace(profile.AdvanceDurationText)
                        ? stored.AdvanceDurationText
                        : profile.AdvanceDurationText,
                    Rating = profile.Rating ?? stored.Rating,
                    ReviewsCount = profile.ReviewsCount ?? stored.ReviewsCount,
                    ReviewsText = string.IsNullOrWhiteSpace(profile.ReviewsText)
                        ? stored.ReviewsText
                        : profile.ReviewsText
                };
            })
            .ToList();
    }
}