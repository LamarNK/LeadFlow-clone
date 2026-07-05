using System.Text.Json;
using Orbita.Contracts;

namespace Orbita.Api.Helpers;

internal static class SubProfileIssueHelper
{
    public static WorkerSubProfileDto ClearIssue(WorkerSubProfileDto profile) =>
        profile with
        {
            LastIssueKind = null,
            LastIssueMessage = null,
            LastIssueAt = null,
            DiagnosticAttachmentId = null
        };

    public static IReadOnlyList<WorkerSubProfileDto>? StripMissingAttachmentIssues(
        IReadOnlyList<WorkerSubProfileDto>? profiles,
        IReadOnlySet<Guid> existingAttachmentIds)
    {
        if (profiles is null || profiles.Count == 0)
        {
            return profiles;
        }

        var changed = false;
        var result = profiles
            .Select(profile =>
            {
                if (profile.DiagnosticAttachmentId is not Guid attachmentId
                    || existingAttachmentIds.Contains(attachmentId))
                {
                    return profile;
                }

                changed = true;
                return ClearIssue(profile);
            })
            .ToList();

        return changed ? result : profiles;
    }

    public static bool TryClearIssueInJson(
        string? subProfilesJson,
        string? subProfileId,
        string? subProfileName,
        out string? updatedJson)
    {
        updatedJson = null;
        var profiles = SubProfileDeserializer.Deserialize(subProfilesJson);
        if (profiles is null || profiles.Count == 0)
        {
            return false;
        }

        var index = FindSubProfileIndex(profiles, subProfileId, subProfileName);
        if (index < 0)
        {
            return false;
        }

        var profile = profiles[index];
        if (string.IsNullOrWhiteSpace(profile.LastIssueKind)
            && string.IsNullOrWhiteSpace(profile.LastIssueMessage)
            && profile.DiagnosticAttachmentId is null)
        {
            return false;
        }

        var updated = profiles.ToList();
        updated[index] = ClearIssue(profile);
        updatedJson = JsonSerializer.Serialize(updated, SubProfileJsonOptions.Serialize);
        return true;
    }

    public static IReadOnlySet<Guid> CollectAttachmentIds(IEnumerable<WorkerSubProfileDto?> profiles) =>
        profiles
            .Where(p => p?.DiagnosticAttachmentId is Guid id && id != Guid.Empty)
            .Select(p => p!.DiagnosticAttachmentId!.Value)
            .ToHashSet();

    private static int FindSubProfileIndex(
        IReadOnlyList<WorkerSubProfileDto> profiles,
        string? subProfileId,
        string? subProfileName)
    {
        if (!string.IsNullOrWhiteSpace(subProfileId))
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                if (string.Equals(profiles[i].Id, subProfileId, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(subProfileName))
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                if (string.Equals(profile.Name, subProfileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(profile.Id, subProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }
}