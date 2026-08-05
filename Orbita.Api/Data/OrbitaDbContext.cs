using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Orbita.Contracts;

namespace Orbita.Api.Data;

public sealed class OrbitaDbContext(DbContextOptions<OrbitaDbContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<OfficeEntity> Offices => Set<OfficeEntity>();
    public DbSet<PanelUserProfileEntity> PanelUserProfiles => Set<PanelUserProfileEntity>();
    public DbSet<WorkerEntity> Workers => Set<WorkerEntity>();
    public DbSet<WorkerSnapshotEntity> WorkerSnapshots => Set<WorkerSnapshotEntity>();
    public DbSet<WorkerAccountEntity> WorkerAccounts => Set<WorkerAccountEntity>();
    public DbSet<WorkerEventEntity> WorkerEvents => Set<WorkerEventEntity>();
    public DbSet<WorkerDiagnosticAttachmentEntity> WorkerDiagnosticAttachments => Set<WorkerDiagnosticAttachmentEntity>();
    public DbSet<WorkerLogEntryEntity> WorkerLogEntries => Set<WorkerLogEntryEntity>();
    public DbSet<PanelAuditLogEntity> PanelAuditLogs => Set<PanelAuditLogEntity>();
    public DbSet<PanelUserBitrixSettingsEntity> PanelUserBitrixSettings => Set<PanelUserBitrixSettingsEntity>();
    public DbSet<CandidatePersonEntity> CandidatePersons => Set<CandidatePersonEntity>();
    public DbSet<CandidatePhoneHistoryEntity> CandidatePhoneHistory => Set<CandidatePhoneHistoryEntity>();
    public DbSet<CandidateResponseEntity> CandidateResponses => Set<CandidateResponseEntity>();
    public DbSet<CrmCandidateCardEntity> CrmCandidateCards => Set<CrmCandidateCardEntity>();
    public DbSet<CrmCandidateNoteEntity> CrmCandidateNotes => Set<CrmCandidateNoteEntity>();
    public DbSet<CrmTaskEntity> CrmTasks => Set<CrmTaskEntity>();
    public DbSet<CrmTaskCommentEntity> CrmTaskComments => Set<CrmTaskCommentEntity>();
    public DbSet<CrmTaskAttachmentEntity> CrmTaskAttachments => Set<CrmTaskAttachmentEntity>();
    public DbSet<CrmCandidateHistoryEntity> CrmCandidateHistory => Set<CrmCandidateHistoryEntity>();
    public DbSet<ResponseBitrixDeliveryEntity> ResponseBitrixDeliveries => Set<ResponseBitrixDeliveryEntity>();
    public DbSet<ResponseCrmDeliveryEntity> ResponseCrmDeliveries => Set<ResponseCrmDeliveryEntity>();
    public DbSet<BitrixInstanceEntity> BitrixInstances => Set<BitrixInstanceEntity>();
    public DbSet<BitrixWorkforceConfigurationEntity> BitrixWorkforceConfigurations => Set<BitrixWorkforceConfigurationEntity>();
    public DbSet<BitrixWorkforceStageRuleEntity> BitrixWorkforceStageRules => Set<BitrixWorkforceStageRuleEntity>();
    public DbSet<BitrixWorkforceManagerEntity> BitrixWorkforceManagers => Set<BitrixWorkforceManagerEntity>();
    public DbSet<BitrixWorkforceEventCredentialEntity> BitrixWorkforceEventCredentials => Set<BitrixWorkforceEventCredentialEntity>();
    public DbSet<BitrixDealEventInboxEntity> BitrixDealEventInbox => Set<BitrixDealEventInboxEntity>();
    public DbSet<BitrixWorkforceCursorEntity> BitrixWorkforceCursors => Set<BitrixWorkforceCursorEntity>();
    public DbSet<BitrixWorkforceDealStateEntity> BitrixWorkforceDealStates => Set<BitrixWorkforceDealStateEntity>();
    public DbSet<BitrixWorkforceMorningStateEntity> BitrixWorkforceMorningStates => Set<BitrixWorkforceMorningStateEntity>();
    public DbSet<BitrixWorkforceAssignmentEntity> BitrixWorkforceAssignments => Set<BitrixWorkforceAssignmentEntity>();
    public DbSet<DistributionRouteEntity> DistributionRoutes => Set<DistributionRouteEntity>();
    public DbSet<DistributionNodeEntity> DistributionNodes => Set<DistributionNodeEntity>();
    public DbSet<DistributionRoundRobinStateEntity> DistributionRoundRobinStates => Set<DistributionRoundRobinStateEntity>();
    public DbSet<CaptchaSessionEntity> CaptchaSessions => Set<CaptchaSessionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OfficeEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.RegistrationSecretHash).HasMaxLength(512);
            entity.Property(x => x.BitrixWebhookUrlProtected).HasMaxLength(2048);
            entity.Property(x => x.BitrixPortalHost).HasMaxLength(256);
            entity.Property(x => x.BitrixValidationStatus).HasMaxLength(32);
            entity.Property(x => x.BitrixValidationMessage).HasMaxLength(2000);
            entity.Property(x => x.BitrixUpdatedByUserId).HasMaxLength(128);
            entity.Property(x => x.CrmStagesJson).HasMaxLength(4000);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<PanelUserProfileEntity>(entity =>
        {
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.FullName).HasMaxLength(256);
            entity.HasOne(x => x.Office)
                .WithMany(x => x.UserProfiles)
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WorkerEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasOne(x => x.Office)
                .WithMany(x => x.Workers)
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.OfficeId);
            entity.HasIndex(x => x.OwnerUserId);
            entity.Property(x => x.OwnerUserId).HasMaxLength(128);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.MachineName).HasMaxLength(200);
            entity.Property(x => x.ApiKeyHash).HasMaxLength(512);
            entity.Property(x => x.AppVersion).HasMaxLength(50);
            entity.Property(x => x.MonitoringStatus).HasMaxLength(50);
            entity.Property(x => x.ActivityPhase).HasMaxLength(32);
            entity.Property(x => x.AdsPowerApiBaseUrl).HasMaxLength(512);
            entity.Property(x => x.AdsPowerApiKey).HasMaxLength(256);
            entity.Property(x => x.ResponseHighlightAgeBuckets).HasMaxLength(256);
            entity.Property(x => x.AutoScheduleDays).HasMaxLength(64);
            entity.Property(x => x.AutoScheduleFromLocalTime).HasMaxLength(5);
            entity.Property(x => x.AutoScheduleToLocalTime).HasMaxLength(5);
            entity.Property(x => x.MessengerAutoReplyMessage).HasMaxLength(2000);
            entity.Property(x => x.PhoneUnchangedHours);
            entity.Property(x => x.LastUpdateVersion).HasMaxLength(50);
            entity.Property(x => x.LastUpdateMessage).HasMaxLength(2000);
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.OperatingSystem).HasMaxLength(256);
            entity.Property(x => x.AgentVersion).HasMaxLength(50);
            entity.HasIndex(x => x.LastSeenAtUtc);
        });

        modelBuilder.Entity<WorkerSnapshotEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WorkerId, x.CapturedAtUtc });
            entity.HasOne(x => x.Worker).WithMany(x => x.Snapshots).HasForeignKey(x => x.WorkerId);
        });

        modelBuilder.Entity<WorkerAccountEntity>(entity =>
        {
            entity.HasKey(x => new { x.WorkerId, x.AccountId });
            entity.Property(x => x.AdsPowerProfileId).HasMaxLength(128);
            entity.Property(x => x.AvitoLogin).HasMaxLength(256);
            entity.Property(x => x.AvitoPasswordProtected).HasMaxLength(2048);
            entity.HasOne(x => x.Worker).WithMany(x => x.Accounts).HasForeignKey(x => x.WorkerId);
        });

        modelBuilder.Entity<CandidatePersonEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.LastName, x.FirstName, x.MiddleName });
            entity.HasIndex(x => x.OfficeId);
            entity.Property(x => x.FullName).HasMaxLength(500);
            entity.Property(x => x.FirstName).HasMaxLength(200);
            entity.Property(x => x.LastName).HasMaxLength(200);
            entity.Property(x => x.MiddleName).HasMaxLength(200);
            entity.Property(x => x.City).HasMaxLength(500);
            entity.Property(x => x.PhoneRaw).HasMaxLength(64);
            entity.Property(x => x.PhoneNormalized).HasMaxLength(32);
            entity.HasOne(x => x.Office).WithMany().HasForeignKey(x => x.OfficeId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CandidatePhoneHistoryEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.PersonId);
            entity.HasIndex(x => new { x.PersonId, x.RecordedAtUtc });
            entity.Property(x => x.PhoneRaw).HasMaxLength(64);
            entity.Property(x => x.PhoneNormalized).HasMaxLength(32);
            entity.HasOne(x => x.Person).WithMany(x => x.PhoneHistory).HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Response).WithMany().HasForeignKey(x => x.ResponseId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CandidateResponseEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.PersonId);
            entity.HasIndex(x => x.CreatedAt);
            entity.HasIndex(x => x.CollectedAt);
            entity.HasIndex(x => x.PhoneNormalized);
            entity.HasIndex(x => new { x.OfficeId, x.PhoneNormalized });
            entity.HasIndex(x => new { x.OfficeId, x.AccountId, x.AvitoSubProfileId, x.PhoneNormalized });
            entity.HasIndex(x => new { x.OfficeId, x.AccountId, x.AvitoSubProfileId, x.CardFingerprint });
            entity.HasIndex(x => new { x.OfficeId, x.CreatedAt });
            entity.HasIndex(x => new { x.OfficeId, x.CollectedAt });
            entity.HasIndex(x => new { x.AccountId, x.SourceResponseId })
                .IsUnique()
                .HasFilter("\"SourceResponseId\" <> ''");
            entity.Property(x => x.DuplicateSummary).HasMaxLength(2000);
            entity.Property(x => x.Gender).HasMaxLength(16);
            entity.Property(x => x.WorkerName).HasMaxLength(200);
            entity.Property(x => x.DistributionMode).HasMaxLength(16);
            entity.Property(x => x.PhoneMetricKind).HasMaxLength(32);
            entity.Property(x => x.PreviousPhoneRaw).HasMaxLength(64);
            entity.Property(x => x.PreviousPhoneNormalized).HasMaxLength(32);
            entity.HasOne(x => x.Person).WithMany(x => x.Responses).HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Office).WithMany().HasForeignKey(x => x.OfficeId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.BitrixInstance).WithMany().HasForeignKey(x => x.BitrixInstanceId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.DuplicateBitrixInstance).WithMany().HasForeignKey(x => x.DuplicateBitrixInstanceId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ResponseCrmDeliveryEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ResponseId);
            entity.HasIndex(x => new { x.ResponseId, x.CreatedAtUtc });
            entity.Property(x => x.Outcome).HasMaxLength(16);
            entity.Property(x => x.Source).HasMaxLength(16);
            entity.Property(x => x.ErrorMessage).HasMaxLength(2000);
            entity.HasOne(x => x.Response).WithMany(x => x.CrmDeliveries).HasForeignKey(x => x.ResponseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Office).WithMany().HasForeignKey(x => x.OfficeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Card).WithMany().HasForeignKey(x => x.CardId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CrmCandidateCardEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ResponseId).IsUnique();
            entity.HasIndex(x => new { x.OfficeId, x.ManagerUserId, x.IsInActiveLoad });
            entity.HasIndex(x => new { x.OfficeId, x.Stage });
            entity.HasIndex(x => new { x.OfficeId, x.IsClosed, x.NextActionAtUtc });
            entity.Property(x => x.Stage).HasMaxLength(64);
            entity.Property(x => x.ManagerUserId).HasMaxLength(128);
            entity.Property(x => x.CloseReason).HasMaxLength(64);
            entity.HasOne(x => x.Response).WithMany().HasForeignKey(x => x.ResponseId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmCandidateNoteEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CardId, x.CreatedAtUtc });
            entity.Property(x => x.AuthorUserId).HasMaxLength(128);
            entity.Property(x => x.AuthorName).HasMaxLength(256);
            entity.Property(x => x.Text).HasMaxLength(4000);
        });

        modelBuilder.Entity<CrmTaskEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OfficeId, x.AssigneeUserId, x.Status });
            entity.HasIndex(x => x.CardId);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.Property(x => x.Description).HasMaxLength(4000);
            entity.Property(x => x.AssigneeUserId).HasMaxLength(128);
            entity.Property(x => x.CreatorUserId).HasMaxLength(128);
            entity.Property(x => x.CreatorName).HasMaxLength(256);
            entity.Property(x => x.Importance).HasMaxLength(16).HasDefaultValue(CrmTaskImportances.Medium);
            entity.Property(x => x.Status).HasMaxLength(16);
        });

        modelBuilder.Entity<CrmTaskCommentEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TaskId, x.CreatedAtUtc });
            entity.Property(x => x.AuthorUserId).HasMaxLength(128);
            entity.Property(x => x.AuthorName).HasMaxLength(256);
            entity.Property(x => x.Text).HasMaxLength(4000);
            entity.HasOne<CrmTaskEntity>()
                .WithMany()
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmTaskAttachmentEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TaskId, x.CreatedAtUtc });
            entity.Property(x => x.FileName).HasMaxLength(255);
            entity.Property(x => x.ContentType).HasMaxLength(128);
            entity.Property(x => x.UploadedByUserId).HasMaxLength(128);
            entity.Property(x => x.UploadedByName).HasMaxLength(256);
            entity.Property(x => x.RelativePath).HasMaxLength(512);
            entity.HasOne<CrmTaskEntity>()
                .WithMany()
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmCandidateHistoryEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CardId, x.CreatedAtUtc });
            entity.Property(x => x.Action).HasMaxLength(64);
            entity.Property(x => x.Details).HasMaxLength(2000);
            entity.Property(x => x.ActorUserId).HasMaxLength(128);
            entity.Property(x => x.ActorName).HasMaxLength(256);
        });
        modelBuilder.Entity<ResponseBitrixDeliveryEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ResponseId);
            entity.HasIndex(x => new { x.ResponseId, x.CreatedAtUtc });
            entity.Property(x => x.Outcome).HasMaxLength(16);
            entity.Property(x => x.Source).HasMaxLength(16);
            entity.Property(x => x.ErrorMessage).HasMaxLength(2000);
            entity.HasOne(x => x.Response).WithMany(x => x.BitrixDeliveries).HasForeignKey(x => x.ResponseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.BitrixInstance).WithMany().HasForeignKey(x => x.BitrixInstanceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BitrixInstanceEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OfficeId);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Signature).HasMaxLength(200);
            entity.Property(x => x.WebhookUrlProtected).HasMaxLength(2048);
            entity.Property(x => x.PortalHost).HasMaxLength(256);
            entity.Property(x => x.ValidationStatus).HasMaxLength(32);
            entity.Property(x => x.ValidationMessage).HasMaxLength(2000);
            entity.Property(x => x.IntegrationSettingsJson).HasMaxLength(4000);
            entity.Property(x => x.UpdatedByUserId).HasMaxLength(128);
            entity.HasOne(x => x.Office).WithMany().HasForeignKey(x => x.OfficeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BitrixWorkforceConfigurationEntity>(entity =>
        {
            entity.HasKey(x => x.BitrixInstanceId);
            entity.Property(x => x.OperationMode).HasMaxLength(16);
            entity.Property(x => x.TimeZoneId).HasMaxLength(128);
            entity.Property(x => x.SingleManagerInitialReleasePercent).HasPrecision(5, 2);
            entity.Property(x => x.UpdatedByUserId).HasMaxLength(128);
            entity.HasOne(x => x.BitrixInstance)
                .WithOne()
                .HasForeignKey<BitrixWorkforceConfigurationEntity>(x => x.BitrixInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BitrixWorkforceStageRuleEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.BitrixInstanceId, x.SourceStageId }).IsUnique();
            entity.Property(x => x.Scenario).HasMaxLength(64);
            entity.Property(x => x.SourceStageId).HasMaxLength(128);
            entity.Property(x => x.TargetStageId).HasMaxLength(128);
            entity.HasOne(x => x.BitrixInstance)
                .WithMany()
                .HasForeignKey(x => x.BitrixInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BitrixWorkforceManagerEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.BitrixInstanceId, x.BitrixUserId }).IsUnique();
            entity.HasIndex(x => new { x.BitrixInstanceId, x.SortOrder });
            entity.HasOne(x => x.BitrixInstance)
                .WithMany()
                .HasForeignKey(x => x.BitrixInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BitrixWorkforceEventCredentialEntity>(entity =>
        {
            entity.HasKey(x => x.BitrixInstanceId);
            entity.HasIndex(x => x.PublicId).IsUnique();
            entity.Property(x => x.ApplicationTokenHash).HasMaxLength(128);
            entity.Property(x => x.ExpectedMemberId).HasMaxLength(128);
            entity.HasOne(x => x.BitrixInstance)
                .WithOne()
                .HasForeignKey<BitrixWorkforceEventCredentialEntity>(x => x.BitrixInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BitrixDealEventInboxEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.BitrixInstanceId, x.EventKey }).IsUnique();
            entity.HasIndex(x => new { x.State, x.NextAttemptAtUtc });
            entity.Property(x => x.EventName).HasMaxLength(64);
            entity.Property(x => x.EventKey).HasMaxLength(128);
            entity.Property(x => x.State).HasMaxLength(32);
            entity.Property(x => x.LockOwner).HasMaxLength(128);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasOne(x => x.BitrixInstance)
                .WithMany()
                .HasForeignKey(x => x.BitrixInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BitrixWorkforceCursorEntity>(entity =>
        {
            entity.HasKey(x => new { x.BitrixInstanceId, x.Scenario, x.OperationMode });
            entity.Property(x => x.Scenario).HasMaxLength(64);
            entity.Property(x => x.OperationMode).HasMaxLength(16);
            entity.HasOne(x => x.BitrixInstance)
                .WithMany()
                .HasForeignKey(x => x.BitrixInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BitrixWorkforceDealStateEntity>(entity =>
        {
            entity.HasKey(x => new { x.BitrixInstanceId, x.DealId });
            entity.Property(x => x.ActiveScenario).HasMaxLength(64);
            entity.Property(x => x.LastObservedStageId).HasMaxLength(128);
            entity.Property(x => x.LastAppliedStageId).HasMaxLength(128);
            entity.HasOne(x => x.BitrixInstance)
                .WithMany()
                .HasForeignKey(x => x.BitrixInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BitrixWorkforceMorningStateEntity>(entity =>
        {
            entity.HasKey(x => new
            {
                x.BitrixInstanceId,
                x.LocalDate,
                x.Scenario,
                x.OperationMode
            });
            entity.Property(x => x.Scenario).HasMaxLength(64);
            entity.Property(x => x.OperationMode).HasMaxLength(16);
            entity.HasOne(x => x.BitrixInstance)
                .WithMany()
                .HasForeignKey(x => x.BitrixInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BitrixWorkforceAssignmentEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.InboxId).IsUnique();
            entity.HasIndex(x => new { x.BitrixInstanceId, x.DealId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.BitrixInstanceId, x.DealId })
                .IsUnique()
                .HasFilter("\"AppliedAtUtc\" IS NULL AND \"Decision\" = 'assigned'");
            entity.Property(x => x.Scenario).HasMaxLength(64);
            entity.Property(x => x.OperationMode).HasMaxLength(16);
            entity.Property(x => x.FromStageId).HasMaxLength(128);
            entity.Property(x => x.ToStageId).HasMaxLength(128);
            entity.Property(x => x.Decision).HasMaxLength(32);
            entity.Property(x => x.Reason).HasMaxLength(1000);
            entity.Property(x => x.Error).HasMaxLength(2000);
            entity.HasOne(x => x.BitrixInstance)
                .WithMany()
                .HasForeignKey(x => x.BitrixInstanceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Inbox)
                .WithOne()
                .HasForeignKey<BitrixWorkforceAssignmentEntity>(x => x.InboxId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DistributionRouteEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OfficeId).IsUnique();
            entity.Property(x => x.UpdatedByUserId).HasMaxLength(128);
            entity.HasOne(x => x.Office).WithMany().HasForeignKey(x => x.OfficeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DistributionNodeEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.RouteId);
            entity.HasIndex(x => new { x.RouteId, x.ParentNodeId, x.SortOrder });
            entity.HasOne(x => x.Route).WithMany(x => x.Nodes).HasForeignKey(x => x.RouteId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.ParentNode).WithMany(x => x.Children).HasForeignKey(x => x.ParentNodeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.BitrixInstance).WithMany().HasForeignKey(x => x.BitrixInstanceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DistributionRoundRobinStateEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.RouteId, x.ParentNodeId }).IsUnique();
            entity.HasOne(x => x.Route).WithMany().HasForeignKey(x => x.RouteId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkerEventEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WorkerId, x.CreatedAtUtc });
            entity.Property(x => x.Level).HasMaxLength(32);
            entity.Property(x => x.Message).HasMaxLength(2000);
            entity.HasOne(x => x.Worker).WithMany(x => x.Events).HasForeignKey(x => x.WorkerId);
        });

        modelBuilder.Entity<WorkerDiagnosticAttachmentEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WorkerId, x.CreatedAtUtc });
            entity.Property(x => x.Kind).HasMaxLength(64);
            entity.Property(x => x.PageUrl).HasMaxLength(2048);
            entity.Property(x => x.RelativePath).HasMaxLength(512);
            entity.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        });

        modelBuilder.Entity<WorkerLogEntryEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WorkerId, x.TimestampUtc });
            entity.HasIndex(x => new { x.WorkerId, x.DedupHash }).IsUnique();
            entity.Property(x => x.Level).HasMaxLength(16);
            entity.Property(x => x.Source).HasMaxLength(256);
            entity.Property(x => x.TraceId).HasMaxLength(64);
            entity.Property(x => x.DedupHash).HasMaxLength(64);
            entity.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        });

        modelBuilder.Entity<PanelUserBitrixSettingsEntity>(entity =>
        {
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.WebhookUrlProtected).HasMaxLength(2048);
            entity.Property(x => x.PortalHost).HasMaxLength(256);
            entity.Property(x => x.ValidationStatus).HasMaxLength(32);
            entity.Property(x => x.ValidationMessage).HasMaxLength(2000);
            entity.Property(x => x.UpdatedByUserId).HasMaxLength(128);
        });

        modelBuilder.Entity<CaptchaSessionEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WorkerId, x.Status });
            entity.Property(x => x.AccountName).HasMaxLength(200);
            entity.Property(x => x.OperatorUserId).HasMaxLength(128);
            entity.Property(x => x.OperatorDisplayName).HasMaxLength(256);
            entity.Property(x => x.PageUrl).HasMaxLength(2048);
            entity.Property(x => x.CaptchaKind).HasMaxLength(64);
            entity.Property(x => x.SubProfileId).HasMaxLength(128);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.FailureMessage).HasMaxLength(2000);
            entity.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        });

        modelBuilder.Entity<PanelAuditLogEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TimestampUtc);
            entity.HasIndex(x => x.Action);
            entity.Property(x => x.Action).HasMaxLength(64);
            entity.Property(x => x.ActorEmail).HasMaxLength(256);
            entity.Property(x => x.TargetType).HasMaxLength(64);
            entity.Property(x => x.TargetId).HasMaxLength(128);
            entity.Property(x => x.Details).HasMaxLength(2000);
            entity.Property(x => x.IpAddress).HasMaxLength(64);
        });
    }
}
