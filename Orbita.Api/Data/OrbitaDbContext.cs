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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OfficeEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.RegistrationSecretHash).HasMaxLength(512);
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
            entity.HasIndex(x => new { x.OfficeId, x.CreatedAt });
            entity.HasIndex(x => new { x.AccountId, x.SourceResponseId })
                .IsUnique()
                .HasFilter("\"SourceResponseId\" <> ''");
            entity.Property(x => x.DuplicateSummary).HasMaxLength(2000);
            entity.HasOne(x => x.Office).WithMany().HasForeignKey(x => x.OfficeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
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