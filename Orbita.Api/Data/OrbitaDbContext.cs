using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

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
    public DbSet<CandidateResponseEntity> CandidateResponses => Set<CandidateResponseEntity>();
    public DbSet<ResponseBitrixDeliveryEntity> ResponseBitrixDeliveries => Set<ResponseBitrixDeliveryEntity>();
    public DbSet<BitrixInstanceEntity> BitrixInstances => Set<BitrixInstanceEntity>();
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
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<PanelUserProfileEntity>(entity =>
        {
            entity.HasKey(x => x.UserId);
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
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.MachineName).HasMaxLength(200);
            entity.Property(x => x.ApiKeyHash).HasMaxLength(512);
            entity.Property(x => x.AppVersion).HasMaxLength(50);
            entity.Property(x => x.MonitoringStatus).HasMaxLength(50);
            entity.Property(x => x.ActivityPhase).HasMaxLength(32);
            entity.Property(x => x.AdsPowerApiBaseUrl).HasMaxLength(512);
            entity.Property(x => x.AdsPowerApiKey).HasMaxLength(256);
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
            entity.HasOne(x => x.Worker).WithMany(x => x.Accounts).HasForeignKey(x => x.WorkerId);
        });

        modelBuilder.Entity<CandidateResponseEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CreatedAt);
            entity.HasIndex(x => x.PhoneNormalized);
            entity.HasIndex(x => new { x.OfficeId, x.PhoneNormalized });
            entity.HasIndex(x => new { x.OfficeId, x.AccountId, x.AvitoSubProfileId, x.PhoneNormalized });
            entity.HasIndex(x => new { x.OfficeId, x.AccountId, x.AvitoSubProfileId, x.CardFingerprint });
            entity.HasIndex(x => new { x.OfficeId, x.CreatedAt });
            entity.HasIndex(x => new { x.AccountId, x.SourceResponseId })
                .IsUnique()
                .HasFilter("\"SourceResponseId\" <> ''");
            entity.Property(x => x.DuplicateSummary).HasMaxLength(2000);
            entity.Property(x => x.WorkerName).HasMaxLength(200);
            entity.Property(x => x.DistributionMode).HasMaxLength(16);
            entity.HasOne(x => x.Office).WithMany().HasForeignKey(x => x.OfficeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.BitrixInstance).WithMany().HasForeignKey(x => x.BitrixInstanceId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.DuplicateBitrixInstance).WithMany().HasForeignKey(x => x.DuplicateBitrixInstanceId).OnDelete(DeleteBehavior.SetNull);
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