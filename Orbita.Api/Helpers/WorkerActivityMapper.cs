using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Helpers;

internal static class WorkerActivityMapper
{
    public static WorkerActivityDto? ToDto(WorkerEntity worker) =>
        ToDto(
            worker.ActivityPhase,
            worker.ActivityMessage,
            worker.ActivityAccountId,
            worker.ActivityAccountName,
            worker.ActivitySubProfileId,
            worker.ActivitySubProfileName,
            worker.ActivityNextCycleAtUtc,
            worker.ActivityUpdatedAtUtc);

    public static WorkerActivityDto? ToDto(
        string? phase,
        string? message,
        Guid? accountId,
        string? accountName,
        string? subProfileId,
        string? subProfileName,
        DateTime? nextCycleAtUtc,
        DateTime? updatedAtUtc)
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
            updatedAtUtc.Value);
    }

    public static bool ActivityChanged(WorkerEntity worker, WorkerActivityRequest request) =>
        !string.Equals(worker.ActivityPhase, request.Phase, StringComparison.Ordinal)
        || !string.Equals(worker.ActivityMessage, request.Message, StringComparison.Ordinal)
        || worker.ActivityAccountId != request.AccountId
        || !string.Equals(worker.ActivityAccountName, request.AccountName, StringComparison.Ordinal)
        || !string.Equals(worker.ActivitySubProfileId, request.SubProfileId, StringComparison.Ordinal)
        || !string.Equals(worker.ActivitySubProfileName, request.SubProfileName, StringComparison.Ordinal);

    public static void Apply(WorkerEntity worker, WorkerActivityRequest request)
    {
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
}