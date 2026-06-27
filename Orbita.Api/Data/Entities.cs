using Orbita.Contracts;

namespace Orbita.Api.Data;

public sealed class OfficeEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RegistrationSecretHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public bool IsEnabled { get; set; } = true;

    public ICollection<WorkerEntity> Workers { get; set; } = [];
    public ICollection<PanelUserProfileEntity> UserProfiles { get; set; } = [];
}

public sealed class PanelUserProfileEntity
{
    public string UserId { get; set; } = string.Empty;
    public Guid? OfficeId { get; set; }

    public OfficeEntity? Office { get; set; }
}

public sealed class WorkerEntity
{
    public Guid Id { get; set; }
    public Guid OfficeId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string ApiKeyHash { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string MonitoringStatus { get; set; } = "Stopped";
    public string? MonitoringStatusMessage { get; set; }
    public bool IsMonitoringActive { get; set; }
    public DateTime? NextCycleCheckAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? ApiKeyRotatedAtUtc { get; set; }
    public int MaxConcurrentAccounts { get; set; } = 1;
    public double? LastCpuPercent { get; set; }
    public double? LastRamPercent { get; set; }
    public long? LastRamUsedMb { get; set; }
    public long? LastRamTotalMb { get; set; }
    public string? AdsPowerApiBaseUrl { get; set; }
    public string? AdsPowerApiKey { get; set; }
    public string? LastUpdateVersion { get; set; }
    public bool? LastUpdateSuccess { get; set; }
    public string? LastUpdateMessage { get; set; }
    public DateTime? LastUpdateAtUtc { get; set; }
    public string? IpAddress { get; set; }
    public string? OperatingSystem { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public string? AgentVersion { get; set; }

    public OfficeEntity Office { get; set; } = null!;
    public ICollection<WorkerSnapshotEntity> Snapshots { get; set; } = [];
    public ICollection<WorkerAccountEntity> Accounts { get; set; } = [];
    public ICollection<WorkerEventEntity> Events { get; set; } = [];
}

public sealed class WorkerSnapshotEntity
{
    public Guid Id { get; set; }
    public Guid WorkerId { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public string StatsJson { get; set; } = "{}";
    public string BalancesJson { get; set; } = "[]";

    public WorkerEntity Worker { get; set; } = null!;
}

public sealed class WorkerAccountEntity
{
    public Guid WorkerId { get; set; }
    public Guid AccountId { get; set; }
    public string AdsPowerProfileId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsEnabledInPanel { get; set; }
    public int ActiveAdsCount { get; set; }
    public int BlockedCount { get; set; }
    public int DraftsCount { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTime? LastMonitoringAt { get; set; }
    public decimal TotalBalance { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public WorkerEntity Worker { get; set; } = null!;
}

public sealed class WorkerEventEntity
{
    public Guid Id { get; set; }
    public Guid WorkerId { get; set; }
    public Guid? AccountId { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public WorkerEntity Worker { get; set; } = null!;
}

public sealed class PanelUserBitrixSettingsEntity
{
    public string UserId { get; set; } = string.Empty;
    public string WebhookUrlProtected { get; set; } = string.Empty;
    public string? PortalHost { get; set; }
    public string ValidationStatus { get; set; } = BitrixValidationStatuses.NotConfigured;
    public string? ValidationMessage { get; set; }
    public DateTime? LastValidatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedByUserId { get; set; }
}

public sealed class CandidateResponseEntity
{
    public Guid Id { get; set; }
    public Guid WorkerId { get; set; }
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SourceResponseId { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string MiddleName { get; set; } = string.Empty;
    public int? Age { get; set; }
    public string PhoneRaw { get; set; } = string.Empty;
    public string PhoneNormalized { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Vacancy { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string VacancyUrl { get; set; } = string.Empty;
    public string MessengerUrl { get; set; } = string.Empty;
    public string AvitoSubProfileId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string BitrixEntityType { get; set; } = string.Empty;
    public string BitrixEntityId { get; set; } = string.Empty;
    public string BitrixContactId { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }

    public WorkerEntity Worker { get; set; } = null!;
}

public sealed class PanelAuditLogEntity
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string? ActorUserId { get; set; }
    public string? ActorEmail { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}