namespace Orbita.Contracts;

public static class BitrixWorkforceInboxStates
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Dead = "dead";
}

public static class BitrixWorkforceDecisions
{
    public const string Assigned = "assigned";
    public const string Ignored = "ignored";
    public const string Deferred = "deferred";
    public const string Reserved = "reserved";
    public const string Failed = "failed";
}

public sealed record BitrixWorkforceStageRuleDto(
    Guid? Id,
    string Scenario,
    string SourceStageId,
    string TargetStageId,
    bool UsesMorningWindow,
    int SortOrder,
    bool IsEnabled);

public sealed record BitrixWorkforceSettingsDto(
    Guid BitrixInstanceId,
    string OperationMode,
    int DealCategoryId,
    string TimeZoneId,
    IReadOnlyList<long> ManagerUserIds,
    IReadOnlyList<BitrixWorkforceStageRuleDto> StageRules,
    int MorningWindowStartMinutes,
    int MorningWindowEndMinutes,
    int LateJoinReserveMinutes,
    decimal SingleManagerInitialReleasePercent,
    int RetryDelaySeconds,
    int MaxAttempts,
    bool PreserveManualNewOwner,
    bool SyncContactOwner,
    bool FillOnlyEmptyAvitoFields,
    bool WriterRulesConfirmed,
    bool EventReceiverConfigured,
    string? EventEndpointUrl,
    string? EventExpectedMemberId,
    DateTime? LastEventAtUtc,
    DateTime UpdatedAtUtc);

public sealed record UpdateBitrixWorkforceSettingsRequest(
    string OperationMode,
    int DealCategoryId,
    string TimeZoneId,
    IReadOnlyList<long> ManagerUserIds,
    IReadOnlyList<BitrixWorkforceStageRuleDto> StageRules,
    int MorningWindowStartMinutes = 480,
    int MorningWindowEndMinutes = 660,
    int LateJoinReserveMinutes = 120,
    decimal SingleManagerInitialReleasePercent = 50m,
    int RetryDelaySeconds = 60,
    int MaxAttempts = 20,
    bool PreserveManualNewOwner = true,
    bool SyncContactOwner = true,
    bool FillOnlyEmptyAvitoFields = true,
    bool WriterRulesConfirmed = false);

public sealed record ConfigureBitrixWorkforceReceiverRequest(
    string ApplicationToken,
    string? ExpectedMemberId);

public sealed record BitrixWorkforceReceiverDto(
    bool IsConfigured,
    string? EventEndpointUrl,
    string? ExpectedMemberId,
    DateTime? LastAcceptedAtUtc);

public sealed record BitrixWorkforceAssignmentDto(
    Guid Id,
    Guid BitrixInstanceId,
    long DealId,
    long? ContactId,
    string Scenario,
    string OperationMode,
    string? FromStageId,
    string? ToStageId,
    long? PreviousResponsibleId,
    long? SelectedResponsibleId,
    string Decision,
    string Reason,
    DateTime CreatedAtUtc,
    DateTime? DealAppliedAtUtc,
    DateTime? ContactsAppliedAtUtc,
    DateTime? AppliedAtUtc,
    string? Error);
