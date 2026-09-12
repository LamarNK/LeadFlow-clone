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
    public DbSet<PanelUserPresenceHourEntity> PanelUserPresenceHours => Set<PanelUserPresenceHourEntity>();
    public DbSet<WorkerEntity> Workers => Set<WorkerEntity>();
    public DbSet<WorkerSettingsTemplateEntity> WorkerSettingsTemplates => Set<WorkerSettingsTemplateEntity>();
    public DbSet<WorkerSnapshotEntity> WorkerSnapshots => Set<WorkerSnapshotEntity>();
    public DbSet<WorkerAccountEntity> WorkerAccounts => Set<WorkerAccountEntity>();
    public DbSet<WorkerAvitoAdEntity> WorkerAvitoAds => Set<WorkerAvitoAdEntity>();
    public DbSet<WorkerEventEntity> WorkerEvents => Set<WorkerEventEntity>();
    public DbSet<WorkerDiagnosticAttachmentEntity> WorkerDiagnosticAttachments => Set<WorkerDiagnosticAttachmentEntity>();
    public DbSet<MonitoringCycleRunEntity> MonitoringCycleRuns => Set<MonitoringCycleRunEntity>();
    public DbSet<MonitoringSubProfileRunEntity> MonitoringSubProfileRuns => Set<MonitoringSubProfileRunEntity>();
    public DbSet<PanelAuditLogEntity> PanelAuditLogs => Set<PanelAuditLogEntity>();
    public DbSet<PanelUserBitrixSettingsEntity> PanelUserBitrixSettings => Set<PanelUserBitrixSettingsEntity>();
    public DbSet<CandidatePersonEntity> CandidatePersons => Set<CandidatePersonEntity>();
    public DbSet<CandidatePhoneHistoryEntity> CandidatePhoneHistory => Set<CandidatePhoneHistoryEntity>();
    public DbSet<CandidatePhoneWatchEntity> CandidatePhoneWatches => Set<CandidatePhoneWatchEntity>();
    public DbSet<CandidateContactPhoneEntity> CandidateContactPhones => Set<CandidateContactPhoneEntity>();
    public DbSet<CandidateResponseEntity> CandidateResponses => Set<CandidateResponseEntity>();
    public DbSet<CrmCandidateCardEntity> CrmCandidateCards => Set<CrmCandidateCardEntity>();
    public DbSet<CrmCandidateNoteEntity> CrmCandidateNotes => Set<CrmCandidateNoteEntity>();
    public DbSet<CrmTaskEntity> CrmTasks => Set<CrmTaskEntity>();
    public DbSet<CrmTaskNotificationEntity> CrmTaskNotifications => Set<CrmTaskNotificationEntity>();
    public DbSet<CrmTaskCommentEntity> CrmTaskComments => Set<CrmTaskCommentEntity>();
    public DbSet<CrmTaskAttachmentEntity> CrmTaskAttachments => Set<CrmTaskAttachmentEntity>();
    public DbSet<CrmSuccessDocumentEntity> CrmSuccessDocuments => Set<CrmSuccessDocumentEntity>();
    public DbSet<CrmCandidateHistoryEntity> CrmCandidateHistory => Set<CrmCandidateHistoryEntity>();
    public DbSet<CrmTelephonyWebhookEntity> CrmTelephonyWebhooks => Set<CrmTelephonyWebhookEntity>();
    public DbSet<CrmTelephonyProviderAccountEntity> CrmTelephonyProviderAccounts => Set<CrmTelephonyProviderAccountEntity>();
    public DbSet<CrmTelephonyProviderAccountBindingEntity> CrmTelephonyProviderAccountBindings => Set<CrmTelephonyProviderAccountBindingEntity>();
    public DbSet<CrmTelephonyUserBindingEntity> CrmTelephonyUserBindings => Set<CrmTelephonyUserBindingEntity>();
    public DbSet<CrmCallEntity> CrmCalls => Set<CrmCallEntity>();
    public DbSet<CrmCallAiInsightEntity> CrmCallAiInsights => Set<CrmCallAiInsightEntity>();
    public DbSet<CrmCardChatReadEntity> CrmCardChatReads => Set<CrmCardChatReadEntity>();
    public DbSet<CrmOutboundChatMessageEntity> CrmOutboundChatMessages => Set<CrmOutboundChatMessageEntity>();
    public DbSet<CrmDeskAlertEntity> CrmDeskAlerts => Set<CrmDeskAlertEntity>();
    public DbSet<CrmManagerShiftEntity> CrmManagerShifts => Set<CrmManagerShiftEntity>();
    public DbSet<CrmDailyDistributionSessionEntity> CrmDailyDistributionSessions => Set<CrmDailyDistributionSessionEntity>();
    public DbSet<CrmDailyDistributionCounterEntity> CrmDailyDistributionCounters => Set<CrmDailyDistributionCounterEntity>();
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
    public DbSet<TopUpSessionEntity> TopUpSessions => Set<TopUpSessionEntity>();

    /// <summary>Capture context when a production action is recorded, not when it is reported.</summary>
    public void AddCrmHistory(CrmCandidateHistoryEntity history)
    {
        var entry = ChangeTracker.Entries<CrmCandidateCardEntity>()
            .FirstOrDefault(x => x.Entity.Id == history.CardId);
        if (entry is not null)
        {
            var card = entry.Entity;
            history.OfficeId = card.OfficeId;
            history.ResponsibleUserId = card.ManagerUserId;
            history.StageAtEvent = card.Stage;
            if (entry.State == EntityState.Added)
            {
                card.EntryOfficeId ??= card.OfficeId;
                card.EntryStage ??= card.Stage;
                if (!string.IsNullOrWhiteSpace(card.InitialManagerUserId)) card.InitialAssignedOfficeId ??= card.OfficeId;
            }
            else
            {
                history.PreviousUserId ??= entry.OriginalValues.GetValue<string?>(nameof(card.ManagerUserId));
                history.PreviousCloseReason ??= entry.OriginalValues.GetValue<string?>(nameof(card.CloseReason));
            }
        }
        CrmCandidateHistory.Add(history);
    }

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
            entity.Property(x => x.CrmDeadlineNotificationsEnabled).HasDefaultValue(true);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<PanelUserProfileEntity>(entity =>
        {
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.AccessVersion).HasDefaultValue(0L);
            entity.Property(x => x.FullName).HasMaxLength(256);
            entity.HasOne(x => x.Office)
                .WithMany(x => x.UserProfiles)
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PanelUserPresenceHourEntity>(entity =>
        {
            entity.HasKey(x => new { x.UserId, x.HourUtc });
            entity.Property(x => x.UserId).HasMaxLength(128);
            entity.HasIndex(x => x.HourUtc);
        });

        modelBuilder.Entity<CrmDailyDistributionSessionEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OfficeId, x.LocalDate }).IsUnique();
            entity.HasIndex(x => new { x.DistributedAtUtc, x.DistributeAfterUtc });
            entity.Property(x => x.ManagerRosterJson).HasMaxLength(8000);
            entity.Property(x => x.LastLeadManagerUserId).HasMaxLength(128);
            entity.HasOne<OfficeEntity>()
                .WithMany()
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmDailyDistributionCounterEntity>(entity =>
        {
            entity.HasKey(x => new { x.OfficeId, x.LocalDate, x.Pool, x.ManagerUserId });
            entity.Property(x => x.Pool).HasMaxLength(CrmDailyDistribution.PoolKeyMaxLength);
            entity.Property(x => x.ManagerUserId).HasMaxLength(128);
            entity.HasOne<OfficeEntity>()
                .WithMany()
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.Cascade);
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
            entity.Property(x => x.RuCaptchaApiKey).HasMaxLength(256);
            entity.Property(x => x.AdsPowerGroupId).HasMaxLength(64);
            entity.Property(x => x.AdsPowerGroupName).HasMaxLength(200);
            entity.Property(x => x.AdsPowerGroupsJson).HasMaxLength(16000);
            entity.Property(x => x.MultiloginLauncherUrl).HasMaxLength(512);
            entity.Property(x => x.MultiloginCloudApiUrl).HasMaxLength(512);
            entity.Property(x => x.MultiloginAutomationToken).HasMaxLength(2048);
            entity.Property(x => x.LocalChromeExecutablePath).HasMaxLength(512);
            entity.Property(x => x.AdsPowerEnabled).HasDefaultValue(true);
            entity.Property(x => x.MultiloginEnabled).HasDefaultValue(true);
            entity.Property(x => x.LocalChromeEnabled).HasDefaultValue(true);
            entity.Property(x => x.IsMonitoringPaused).HasDefaultValue(false);
            entity.Property(x => x.TopUpPauseLeaseId);
            entity.Property(x => x.TopUpPauseLeaseVersion).HasDefaultValue(0L);
            entity.Property(x => x.TopUpPauseBaselinePaused).HasDefaultValue(false);
            entity.Property(x => x.PendingBrowserProviderCheck).HasMaxLength(32);
            entity.Property(x => x.PendingBrowserProviderSync).HasMaxLength(32);
            entity.Property(x => x.BrowserProviderChecksJson).HasMaxLength(4000);
            entity.Property(x => x.ResponseHighlightAgeBuckets).HasMaxLength(256);
            entity.Property(x => x.ResponseHighlightTargetsJson).HasMaxLength(16000);
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

        modelBuilder.Entity<WorkerSettingsTemplateEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasOne(x => x.Office)
                .WithMany()
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.NameNormalized).HasMaxLength(200);
            entity.HasIndex(x => new { x.OfficeId, x.NameNormalized }).IsUnique();
            entity.Property(x => x.ResponseHighlightAgeBuckets).HasMaxLength(256);
            entity.Property(x => x.AutoScheduleDays).HasMaxLength(64);
            entity.Property(x => x.AutoScheduleFromLocalTime).HasMaxLength(5);
            entity.Property(x => x.AutoScheduleToLocalTime).HasMaxLength(5);
            entity.Property(x => x.MessengerAutoReplyMessage).HasMaxLength(2000);
            entity.Property(x => x.AdsPowerEnabled).HasDefaultValue(true);
            entity.Property(x => x.MultiloginEnabled).HasDefaultValue(true);
            entity.Property(x => x.LocalChromeEnabled).HasDefaultValue(true);
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
            entity.Property(x => x.AdsPowerGroupId).HasMaxLength(64);
            entity.Property(x => x.AdsPowerGroupName).HasMaxLength(200);
            entity.Property(x => x.MultiloginProfileId).HasMaxLength(128);
            entity.Property(x => x.MultiloginProfileName).HasMaxLength(200);
            entity.Property(x => x.MultiloginFolderId).HasMaxLength(128);
            entity.Property(x => x.LocalUserDataDir).HasMaxLength(1024);
            entity.Property(x => x.AvitoLogin).HasMaxLength(256);
            entity.Property(x => x.AvitoPasswordProtected).HasMaxLength(2048);
            entity.Property(x => x.LocalProxyEnabled).IsRequired().HasDefaultValue(false);
            entity.Property(x => x.LocalProxyAddress).HasMaxLength(255);
            entity.Property(x => x.LocalProxyUsername).HasMaxLength(255);
            entity.Property(x => x.LocalProxyPasswordProtected).HasMaxLength(2048);
            entity.Property(x => x.LocalTrafficMode).IsRequired().HasMaxLength(16).HasDefaultValue("Normal");
            entity.Property(x => x.LocalBlockMedia).IsRequired().HasDefaultValue(false);
            entity.Property(x => x.LocalBlockAnalytics).IsRequired().HasDefaultValue(false);
            entity.Property(x => x.LocalBlockImages).IsRequired().HasDefaultValue(false);
            entity.Property(x => x.LocalBlockFonts).IsRequired().HasDefaultValue(false);
            entity.Property(x => x.LocalBlockPrefetch).IsRequired().HasDefaultValue(false);
            entity.Property(x => x.LocalNavigationTimeoutSeconds).IsRequired().HasDefaultValue(60);
            entity.Property(x => x.LocalTrafficBlockedMedia).IsRequired().HasDefaultValue(0);
            entity.Property(x => x.LocalTrafficBlockedImages).IsRequired().HasDefaultValue(0);
            entity.Property(x => x.LocalTrafficBlockedFonts).IsRequired().HasDefaultValue(0);
            entity.Property(x => x.LocalTrafficBlockedAnalytics).IsRequired().HasDefaultValue(0);
            entity.Property(x => x.LocalTrafficBlockedPrefetch).IsRequired().HasDefaultValue(0);
            entity.HasOne(x => x.Worker).WithMany(x => x.Accounts).HasForeignKey(x => x.WorkerId);
        });

        modelBuilder.Entity<WorkerAvitoAdEntity>(entity =>
        {
            entity.ToTable("WorkerAvitoAds");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WorkerId, x.AccountId, x.AvitoSubProfileId, x.AvitoItemId }).IsUnique();
            entity.HasIndex(x => new { x.WorkerId, x.AccountId, x.IsActive });
            entity.HasIndex(x => new { x.State, x.ExpiresAtUtc });
            entity.Property(x => x.AvitoSubProfileId).HasMaxLength(128);
            entity.Property(x => x.AvitoItemId).HasMaxLength(32);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.Property(x => x.Url).HasMaxLength(1024);
            entity.Property(x => x.StatusText).HasMaxLength(500);
            entity.Property(x => x.PublicationDateSource).HasMaxLength(32);
            entity.Property(x => x.State).HasMaxLength(32);
            entity.Property(x => x.LastParseError).HasMaxLength(500);
            entity.HasOne(x => x.Worker)
                .WithMany()
                .HasForeignKey(x => x.WorkerId)
                .OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<CandidateContactPhoneEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.PersonId);
            entity.HasIndex(x => new { x.PersonId, x.PhoneNormalized }).IsUnique();
            entity.Property(x => x.PhoneRaw).HasMaxLength(64);
            entity.Property(x => x.PhoneNormalized).HasMaxLength(32);
            entity.Property(x => x.Label).HasMaxLength(64);
            entity.Property(x => x.CreatedByUserId).HasMaxLength(128);
            entity.HasOne(x => x.Person).WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Cascade);
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
            entity.HasIndex(x => new { x.WorkerId, x.CollectedAt });
            entity.HasIndex(x => new { x.AccountId, x.SourceResponseId })
                .IsUnique()
                .HasFilter("\"SourceResponseId\" <> ''");
            entity.Property(x => x.DuplicateSummary).HasMaxLength(2000);
            entity.Property(x => x.Gender).HasMaxLength(16);
            entity.Property(x => x.Citizenship).HasMaxLength(CandidateCitizenshipResolver.MaxLength);
            entity.Property(x => x.OperatorLockedFields).HasMaxLength(256);
            entity.Property(x => x.WorkerName).HasMaxLength(200);
            entity.Property(x => x.DistributionMode).HasMaxLength(16);
            entity.Property(x => x.PhoneMetricKind).HasMaxLength(32);
            entity.Property(x => x.PreviousPhoneRaw).HasMaxLength(64);
            entity.Property(x => x.PreviousPhoneNormalized).HasMaxLength(32);
            entity.Property(x => x.AvatarContentType).HasMaxLength(32);
            entity.HasOne(x => x.Person).WithMany(x => x.Responses).HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Office).WithMany().HasForeignKey(x => x.OfficeId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.BitrixInstance).WithMany().HasForeignKey(x => x.BitrixInstanceId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.DuplicateBitrixInstance).WithMany().HasForeignKey(x => x.DuplicateBitrixInstanceId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CandidatePhoneWatchEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.AvitoSubProfileId, x.FullNameKey }).IsUnique();
            entity.HasIndex(x => new { x.WorkerId, x.State, x.ExpiresAtUtc });
            entity.HasIndex(x => x.PersonId);
            entity.HasIndex(x => x.CanonicalResponseId);
            entity.Property(x => x.AvitoSubProfileId).HasMaxLength(128);
            entity.Property(x => x.FullName).HasMaxLength(300);
            entity.Property(x => x.FullNameKey).HasMaxLength(300);
            entity.Property(x => x.PublishedSourceResponseId).HasMaxLength(64);
            entity.Property(x => x.CurrentPhoneRaw).HasMaxLength(64);
            entity.Property(x => x.CurrentPhoneNormalized).HasMaxLength(32);
            entity.Property(x => x.LastPublishedPhoneNormalized).HasMaxLength(32);
            entity.Property(x => x.State).HasMaxLength(16);
            entity.Property(x => x.MessengerUrl).HasMaxLength(2000);
            entity.Property(x => x.ChatFingerprint).HasMaxLength(64);
            entity.Property(x => x.ProfileFingerprint).HasMaxLength(64);
            entity.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Person).WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.CanonicalResponse).WithMany().HasForeignKey(x => x.CanonicalResponseId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ResponseCrmDeliveryEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ResponseId);
            entity.HasIndex(x => new { x.ResponseId, x.CreatedAtUtc });
            // Statistics/dashboard: filter successful sends by send time.
            entity.HasIndex(x => new { x.Outcome, x.CreatedAtUtc });
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
            // A request started before an office transfer/reopening must not overwrite it.
            entity.Property(x => x.OfficeId).IsConcurrencyToken();
            entity.Property(x => x.IsClosed).IsConcurrencyToken();
            entity.HasIndex(x => x.ResponseId).IsUnique();
            entity.HasIndex(x => new { x.OfficeId, x.EnteredCrmAtUtc });
            entity.HasIndex(x => new { x.OfficeId, x.ManagerUserId, x.IsInActiveLoad });
            entity.HasIndex(x => new { x.OfficeId, x.InitialManagerUserId, x.InitialAssignedAtUtc })
                .HasDatabaseName("IX_CrmCards_Office_InitialManager_AssignedAt");
            entity.HasIndex(x => new { x.OfficeId, x.Stage });
            entity.HasIndex(x => new { x.OfficeId, x.IsClosed, x.NextActionAtUtc });
            entity.Property(x => x.Stage).HasMaxLength(64);
            entity.Property(x => x.EntryStage).HasMaxLength(64);
            entity.HasIndex(x => new { x.EntryOfficeId, x.EnteredCrmAtUtc });
            entity.Property(x => x.ManagerUserId).HasMaxLength(128);
            entity.Property(x => x.InitialManagerUserId).HasMaxLength(128);
            entity.Property(x => x.CloseReason).HasMaxLength(64);
            entity.Property(x => x.SuccessContractMissingReason).HasMaxLength(2000);
            entity.HasOne(x => x.Response).WithMany().HasForeignKey(x => x.ResponseId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmCandidateNoteEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CardId, x.IsPinned, x.CreatedAtUtc });
            entity.Property(x => x.AuthorUserId).HasMaxLength(128);
            entity.Property(x => x.AuthorName).HasMaxLength(256);
            entity.Property(x => x.Text).HasMaxLength(4000);
            entity.Property(x => x.IsPinned).HasDefaultValue(false);
        });

        modelBuilder.Entity<CrmTaskEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OfficeId, x.AssigneeUserId, x.Status });
            entity.HasIndex(x => new { x.OfficeId, x.Status, x.DueAtUtc });
            entity.HasIndex(x => x.CardId);
            entity.Property(x => x.Title).HasMaxLength(500);
            entity.Property(x => x.Description).HasMaxLength(4000);
            entity.Property(x => x.AssigneeUserId).HasMaxLength(128);
            entity.Property(x => x.CreatorUserId).HasMaxLength(128);
            entity.Property(x => x.CreatorName).HasMaxLength(256);
            entity.Property(x => x.Importance).HasMaxLength(16).HasDefaultValue(CrmTaskImportances.Medium);
            entity.Property(x => x.TaskType).HasMaxLength(32).HasDefaultValue(CrmTaskTypes.Unspecified);
            entity.Property(x => x.Status).HasMaxLength(16);
        });

        modelBuilder.Entity<CrmTaskNotificationEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TaskId, x.ReminderVersion, x.Kind }).IsUnique();
            entity.HasIndex(x => new { x.OfficeId, x.RecipientUserId, x.ReadAtUtc, x.CreatedAtUtc });
            entity.Property(x => x.RecipientUserId).HasMaxLength(128);
            entity.Property(x => x.Kind).HasMaxLength(16);
            entity.HasOne<CrmTaskEntity>()
                .WithMany()
                .HasForeignKey(x => x.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<OfficeEntity>()
                .WithMany()
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<CrmSuccessDocumentEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CardId, x.Category, x.CreatedAtUtc });
            entity.Property(x => x.Category).HasMaxLength(32);
            entity.Property(x => x.FileName).HasMaxLength(255);
            entity.Property(x => x.ContentType).HasMaxLength(128);
            entity.Property(x => x.UploadedByUserId).HasMaxLength(128);
            entity.Property(x => x.UploadedByName).HasMaxLength(256);
            entity.Property(x => x.RelativePath).HasMaxLength(512);
            entity.HasOne<CrmCandidateCardEntity>()
                .WithMany()
                .HasForeignKey(x => x.CardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmCandidateHistoryEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CardId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.Action, x.CreatedAtUtc, x.CardId })
                .HasDatabaseName("IX_CrmHistory_Action_CreatedAt_Card");
            entity.Property(x => x.Action).HasMaxLength(64);
            entity.Property(x => x.Details).HasMaxLength(2000);
            entity.Property(x => x.TargetUserId).HasMaxLength(128);
            entity.Property(x => x.ResponsibleUserId).HasMaxLength(128);
            entity.Property(x => x.PreviousUserId).HasMaxLength(128);
            entity.Property(x => x.StageAtEvent).HasMaxLength(64);
            entity.Property(x => x.PreviousCloseReason).HasMaxLength(64);
            entity.HasIndex(x => new { x.OfficeId, x.CreatedAtUtc, x.Action });
            entity.Property(x => x.ActorUserId).HasMaxLength(128);
            entity.Property(x => x.ActorName).HasMaxLength(256);
        });

        modelBuilder.Entity<CrmTelephonyWebhookEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.PublicId).IsUnique();
            entity.HasIndex(x => new { x.OfficeId, x.Provider }).IsUnique();
            entity.Property(x => x.Provider).HasMaxLength(32);
            entity.Property(x => x.SecretHash).HasMaxLength(128);
            entity.Property(x => x.ProviderClientId).HasMaxLength(128);
            entity.Property(x => x.ProviderAccessTokenProtected).HasMaxLength(8192);
            entity.Property(x => x.SipAccountProtected).HasMaxLength(8192);
            entity.HasOne<OfficeEntity>()
                .WithMany()
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmTelephonyProviderAccountEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.PublicId).IsUnique();
            entity.HasIndex(x => new { x.OfficeId, x.Provider, x.Name }).IsUnique();
            entity.HasIndex(x => new { x.OfficeId, x.Provider, x.ExternalAccountId })
                .IsUnique()
                .HasFilter("\"ExternalAccountId\" IS NOT NULL");
            entity.HasIndex(x => new { x.Provider, x.IsEnabled, x.SyncCursorUtc });
            entity.Property(x => x.Provider).HasMaxLength(32);
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.ExternalAccountId).HasMaxLength(128);
            entity.Property(x => x.AccessTokenProtected).HasMaxLength(8192);
            entity.Property(x => x.OwnedNumbersJson).HasMaxLength(8192);
            entity.Property(x => x.SecretHash).HasMaxLength(128);
            entity.Property(x => x.SyncStatus).HasMaxLength(32);
            entity.Property(x => x.LastSyncError).HasMaxLength(1024);
            entity.HasOne<OfficeEntity>()
                .WithMany()
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmTelephonyProviderAccountBindingEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ProviderAccountId, x.ProviderUserKey }).IsUnique();
            entity.HasIndex(x => new { x.ProviderAccountId, x.UserId }).IsUnique();
            entity.Property(x => x.ProviderUserKey).HasMaxLength(128);
            entity.Property(x => x.UserId).HasMaxLength(128);
            entity.HasOne<CrmTelephonyProviderAccountEntity>()
                .WithMany()
                .HasForeignKey(x => x.ProviderAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmTelephonyUserBindingEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OfficeId, x.Provider, x.ProviderUserKey }).IsUnique();
            entity.HasIndex(x => new { x.Provider, x.ProviderUserKey })
                .HasDatabaseName("IX_CrmTelephonyUserBindings_Asterisk_ProviderUserKey")
                .HasFilter($"\"{nameof(CrmTelephonyUserBindingEntity.Provider)}\" = '{CrmTelephonyProviders.Asterisk}'")
                .IsUnique();
            entity.HasIndex(x => new { x.OfficeId, x.Provider, x.UserId }).IsUnique();
            entity.Property(x => x.Provider).HasMaxLength(32);
            entity.Property(x => x.ProviderUserKey).HasMaxLength(128);
            entity.Property(x => x.OutboundProvider).HasMaxLength(32);
            entity.Property(x => x.WebRtcAuthorizationUsername).HasMaxLength(128);
            entity.Property(x => x.WebRtcPasswordProtected).HasMaxLength(8192);
            entity.Property(x => x.UserId).HasMaxLength(128);
            entity.HasOne<OfficeEntity>()
                .WithMany()
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmCallEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OfficeId, x.Provider, x.ExternalCallId })
                .HasFilter("\"ProviderAccountId\" IS NULL")
                .IsUnique();
            entity.HasIndex(x => new { x.ProviderAccountId, x.ExternalCallId })
                .HasFilter("\"ProviderAccountId\" IS NOT NULL")
                .IsUnique();
            entity.HasIndex(x => new { x.CardId, x.StartedAtUtc });
            entity.HasIndex(x => new { x.OfficeId, x.ClientPhoneNormalized, x.StartedAtUtc });
            entity.HasIndex(x => new { x.Provider, x.NextRecordingFetchAtUtc });
            entity.HasIndex(x => new { x.Provider, x.NextRecordingArchiveAtUtc });
            entity.Property(x => x.Provider).HasMaxLength(32);
            entity.Property(x => x.ExternalCallId).HasMaxLength(128);
            entity.Property(x => x.Direction).HasMaxLength(32);
            entity.Property(x => x.CallerPhone).HasMaxLength(64);
            entity.Property(x => x.CalledPhone).HasMaxLength(64);
            entity.Property(x => x.ClientPhoneNormalized).HasMaxLength(32);
            entity.Property(x => x.ProviderUserKey).HasMaxLength(128);
            entity.Property(x => x.ManagerUserId).HasMaxLength(128);
            entity.Property(x => x.Status).HasMaxLength(16);
            entity.Property(x => x.Disposition).HasMaxLength(32);
            entity.Property(x => x.DialStatus).HasMaxLength(32);
            entity.HasOne<CrmTelephonyProviderAccountEntity>()
                .WithMany()
                .HasForeignKey(x => x.ProviderAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(x => x.RecordingUrl).HasMaxLength(2048);
            entity.Property(x => x.RecordingStoragePath).HasMaxLength(512);
            entity.Property(x => x.RecordingContentType).HasMaxLength(128);
            entity.Property(x => x.RecordingFileName).HasMaxLength(256);
            entity.HasOne<OfficeEntity>()
                .WithMany()
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CrmCandidateCardEntity>()
                .WithMany()
                .HasForeignKey(x => x.CardId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CrmCallAiInsightEntity>(entity =>
        {
            entity.HasKey(x => x.CallId);
            entity.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.TranscriptText).HasColumnType("text");
            entity.Property(x => x.SegmentsJson).HasColumnType("jsonb");
            entity.Property(x => x.AnalysisJson).HasColumnType("jsonb");
            entity.Property(x => x.AnalysisRawText).HasColumnType("text");
            entity.Property(x => x.PromptVersion).HasMaxLength(32);
            entity.Property(x => x.LastErrorCode).HasMaxLength(64);
            entity.Property(x => x.LastErrorMessage).HasMaxLength(500);
            entity.HasOne(x => x.Call)
                .WithOne()
                .HasForeignKey<CrmCallAiInsightEntity>(x => x.CallId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmCardChatReadEntity>(entity =>
        {
            entity.HasKey(x => new { x.CardId, x.UserId });
            entity.Property(x => x.UserId).HasMaxLength(128);
            entity.Property(x => x.ContentHash).HasMaxLength(64);
            entity.HasOne<CrmCandidateCardEntity>()
                .WithMany()
                .HasForeignKey(x => x.CardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmOutboundChatMessageEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ResponseId, x.Status, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.CardId, x.CreatedAtUtc });
            entity.Property(x => x.AuthorUserId).HasMaxLength(128);
            entity.Property(x => x.AuthorName).HasMaxLength(256);
            entity.Property(x => x.Text).HasMaxLength(CrmOutboundChatStatuses.MaxTextLength);
            entity.Property(x => x.Status).HasMaxLength(16);
            entity.Property(x => x.DeliveryClaimedByWorkerId).HasMaxLength(128);
            entity.HasOne<CrmCandidateCardEntity>()
                .WithMany()
                .HasForeignKey(x => x.CardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmDeskAlertEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OfficeId, x.RecipientUserId, x.ReadAtUtc, x.CreatedAtUtc });
            entity.Property(x => x.RecipientUserId).HasMaxLength(128);
            entity.Property(x => x.Kind).HasMaxLength(32);
            entity.Property(x => x.Title).HasMaxLength(256);
            entity.Property(x => x.Message).HasMaxLength(1000);
            entity.HasOne<OfficeEntity>()
                .WithMany()
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CrmCandidateCardEntity>()
                .WithMany()
                .HasForeignKey(x => x.CardId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CrmManagerShiftEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ManagerUserId).HasMaxLength(128);
            entity.Property(x => x.EndReason).HasMaxLength(32);
            entity.Property(x => x.EndedByUserId).HasMaxLength(128);
            // Активные смены менеджера + история по менеджеру.
            entity.HasIndex(x => new { x.ManagerUserId, x.EndedAtUtc });
            // Аналитика по офису / периоду.
            entity.HasIndex(x => new { x.OfficeId, x.StartedAtUtc });
            entity.HasOne(x => x.Office)
                .WithMany()
                .HasForeignKey(x => x.OfficeId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ResponseBitrixDeliveryEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ResponseId);
            entity.HasIndex(x => new { x.ResponseId, x.CreatedAtUtc });
            // Statistics: filter successful sends by send time.
            entity.HasIndex(x => new { x.Outcome, x.CreatedAtUtc });
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
            entity.HasIndex(x => new { x.OfficeId, x.DeletedAtUtc });
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Signature).HasMaxLength(200);
            entity.Property(x => x.WebhookUrlProtected).HasMaxLength(2048);
            entity.Property(x => x.PortalHost).HasMaxLength(256);
            entity.Property(x => x.ValidationStatus).HasMaxLength(32);
            entity.Property(x => x.ValidationMessage).HasMaxLength(2000);
            entity.Property(x => x.IntegrationSettingsJson).HasMaxLength(4000);
            entity.Property(x => x.UpdatedByUserId).HasMaxLength(128);
            entity.Ignore(x => x.IsDeleted);
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

        modelBuilder.Entity<MonitoringCycleRunEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WorkerId, x.StartedAtUtc });
            entity.HasIndex(x => new { x.AccountId, x.StartedAtUtc });
            entity.Property(x => x.AccountName).HasMaxLength(200);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
            entity.HasMany(x => x.SubProfileRuns)
                .WithOne(x => x.CycleRun)
                .HasForeignKey(x => x.CycleRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MonitoringSubProfileRunEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CycleRunId, x.Position });
            entity.HasIndex(x => x.StartedAtUtc);
            entity.Property(x => x.SubProfileId).HasMaxLength(128);
            entity.Property(x => x.SubProfileName).HasMaxLength(200);
            entity.Property(x => x.Outcome).HasMaxLength(32);
            entity.Property(x => x.ErrorType).HasMaxLength(64);
            entity.Property(x => x.ErrorMessage).HasMaxLength(500);
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

        modelBuilder.Entity<TopUpSessionEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WorkerId, x.Status });
            entity.HasIndex(x => new { x.WorkerId, x.AccountId, x.Status });
            // Идемпотентность: не более одной активной сессии на аккаунт (глобально по AccountId).
            entity.HasIndex(x => new { x.AccountId, x.SubProfileId })
                .HasDatabaseName("IX_TopUpSessions_Account_Active")
                .IsUnique()
                .HasFilter("\"Status\" IN ('requested', 'started', 'payment_claimed', 'qr_ready', 'awaiting_balance')");
            entity.Property(x => x.AccountName).HasMaxLength(200);
            entity.Property(x => x.SubProfileId).HasMaxLength(128);
            entity.Property(x => x.SubProfileName).HasMaxLength(200);
            entity.Property(x => x.OperatorUserId).HasMaxLength(128);
            entity.Property(x => x.OperatorDisplayName).HasMaxLength(256);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.CurrentBalance).HasPrecision(18, 2);
            entity.Property(x => x.TargetBalance).HasPrecision(18, 2);
            entity.Property(x => x.RequestedAmount).HasPrecision(18, 2);
            entity.Property(x => x.BalanceAfter).HasPrecision(18, 2);
            entity.Property(x => x.ExpectedPauseLeaseVersion).HasDefaultValue(0L);
            entity.Property(x => x.OwnsPauseLease).HasDefaultValue(false);
            entity.Property(x => x.QrImageUrl).HasMaxLength(2048);
            entity.Property(x => x.QrImageBase64).HasColumnType("text");
            entity.Property(x => x.FailureMessage).HasMaxLength(2000);
            entity.Property(x => x.ProgressMessage).HasMaxLength(200);
            entity.Property(x => x.RowVersion)
                .IsRowVersion()
                .HasColumnName("xmin");
            entity.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        });
    }
}
