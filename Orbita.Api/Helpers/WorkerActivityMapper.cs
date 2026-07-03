using System.Text.Json;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Helpers;

internal static class WorkerActivityMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static WorkerActivityDto? ToDto(WorkerEntity worker) =>
        ToDto(
            worker.ActivityPhase,
            worker.ActivityMessage,
            worker.ActivityAccountId,
            worker.ActivityAccountName,
            worker.ActivitySubProfileId,
            worker.ActivitySubProfileName,
            worker.ActivityNextCycleAtUtc,
            worker.ActivityUpdatedAtUtc,
            DeserializeActiveAccounts(worker.ActivityActiveAccountsJson));

    public static WorkerActivityDto? ToDto(
        string? phase,
        string? message,
        Guid? accountId,
        string? accountName,
        string? subProfileId,
        string? subProfileName,
        DateTime? nextCycleAtUtc,
        DateTime? updatedAtUtc,
        IReadOnlyList<WorkerActiveAccountDto>? activeAccounts = null)
    {
        if (updatedAtUtc is null
            || string.IsNullOrWhiteSpace(phase)
            || string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return new WorkerActivityDto(
            phase,
            message,
            accountId,
            accountName,
            subProfileId,
            subProfileName,
            nextCycleAtUtc,
            updatedAtUtc.Value,
            activeAccounts ?? []);
    }

    public static IReadOnlyList<WorkerActiveAccountDto> DeserializeActiveAccounts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<WorkerActiveAccountDto>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool ActivityChanged(WorkerEntity worker, WorkerActivityRequest request)
    {
        if (!ActiveAccountsEqual(
                DeserializeActiveAccounts(worker.ActivityActiveAccountsJson),
                request.ActiveAccounts ?? []))
        {
            return true;
        }

        return !string.Equals(worker.ActivityPhase, request.Phase, StringComparison.Ordinal)
            || !string.Equals(worker.ActivityMessage, request.Message, StringComparison.Ordinal)
            || worker.ActivityAccountId != request.AccountId
            || !string.Equals(worker.ActivityAccountName, request.AccountName, StringComparison.Ordinal)
            || !string.Equals(worker.ActivitySubProfileId, request.SubProfileId, StringComparison.Ordinal)
            || !string.Equals(worker.ActivitySubProfileName, request.SubProfileName, StringComparison.Ordinal)
            || worker.ActivityNextCycleAtUtc != request.NextCycleAtUtc;
    }

    public static void Apply(WorkerEntity worker, WorkerActivityRequest request)
    {
        var activeAccounts = request.ActiveAccounts ?? [];
        worker.ActivityActiveAccountsJson = JsonSerializer.Serialize(activeAccounts, JsonOptions);
        worker.ActivityPhase = request.Phase.Trim();
        worker.ActivityMessage = request.Message.Trim();
        worker.ActivityAccountId = request.AccountId;
        worker.ActivityAccountName = string.IsNullOrWhiteSpace(request.AccountName)
            ? null
            : request.AccountName.Trim();
        worker.ActivitySubProfileId = string.IsNullOrWhiteSpace(request.SubProfileId)
            ? null
            : request.SubProfileId.Trim();
        worker.ActivitySubProfileName = string.IsNullOrWhiteSpace(request.SubProfileName)
            ? null
            : request.SubProfileName.Trim();
        worker.ActivityNextCycleAtUtc = DateTimeUtcHelper.EnsureUtc(request.NextCycleAtUtc);
        worker.ActivityUpdatedAtUtc = request.UpdatedAtUtc == default
            ? DateTime.UtcNow
            : DateTimeUtcHelper.EnsureUtc(request.UpdatedAtUtc);
    }

    private static bool ActiveAccountsEqual(
        IReadOnlyList<WorkerActiveAccountDto> left,
        IReadOnlyList<WorkerActiveAccountDto> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a.AccountId != b.AccountId
                || !string.Equals(a.AccountName, b.AccountName, StringComparison.Ordinal)
                || !string.Equals(a.Phase, b.Phase, StringComparison.Ordinal)
                || !string.Equals(a.Message, b.Message, StringComparison.Ordinal)
                || !string.Equals(a.SubProfileId, b.SubProfileId, StringComparison.Ordinal)
                || !string.Equals(a.SubProfileName, b.SubProfileName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}