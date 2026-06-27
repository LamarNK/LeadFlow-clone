using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Orbita.Api.Data;

public sealed class OrbitaDbContext(DbContextOptions<OrbitaDbContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<WorkerEntity> Workers => Set<WorkerEntity>();
    public DbSet<WorkerSnapshotEntity> WorkerSnapshots => Set<WorkerSnapshotEntity>();
    public DbSet<WorkerAccountEntity> WorkerAccounts => Set<WorkerAccountEntity>();
    public DbSet<WorkerEventEntity> WorkerEvents => Set<WorkerEventEntity>();
    public DbSet<PanelAuditLogEntity> PanelAuditLogs => Set<PanelAuditLogEntity>();
    public DbSet<PanelUserBitrixSettingsEntity> PanelUserBitrixSettings => Set<PanelUserBitrixSettingsEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<WorkerEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.MachineName).HasMaxLength(200);
            entity.Property(x => x.ApiKeyHash).HasMaxLength(512);
            entity.Property(x => x.AppVersion).HasMaxLength(50);
            entity.Property(x => x.MonitoringStatus).HasMaxLength(50);
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
            entity.HasOne(x => x.Worker).WithMany(x => x.Accounts).HasForeignKey(x => x.WorkerId);
        });

        modelBuilder.Entity<WorkerEventEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WorkerId, x.CreatedAtUtc });
            entity.Property(x => x.Level).HasMaxLength(32);
            entity.Property(x => x.Message).HasMaxLength(2000);
            entity.HasOne(x => x.Worker).WithMany(x => x.Events).HasForeignKey(x => x.WorkerId);
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