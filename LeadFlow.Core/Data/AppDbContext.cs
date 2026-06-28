using Microsoft.EntityFrameworkCore;

namespace LeadFlow.Core.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<CandidateResponseEntity> CandidateResponses => Set<CandidateResponseEntity>();
    public DbSet<ProcessingLogEntity> ProcessingLogs => Set<ProcessingLogEntity>();
    public DbSet<AvitoAccountEntity> AvitoAccounts => Set<AvitoAccountEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CandidateResponseEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<CandidateResponseEntity>().HasIndex(x => x.CreatedAt);
        modelBuilder.Entity<CandidateResponseEntity>().HasIndex(x => x.PhoneNormalized);
        modelBuilder.Entity<CandidateResponseEntity>().HasIndex(x => new { x.AccountId, x.PhoneNormalized });
        // Уникальность только для непустого SourceResponseId (после trim), иначе несколько откликов без ID источника на один аккаунт невозможны.
        modelBuilder.Entity<CandidateResponseEntity>()
            .HasIndex(x => new { x.AccountId, x.SourceResponseId })
            .IsUnique()
            .HasFilter("length(trim(SourceResponseId)) > 0");
        modelBuilder.Entity<ProcessingLogEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<AvitoAccountEntity>().HasKey(x => x.Id);
    }
}
