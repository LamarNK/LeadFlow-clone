using Microsoft.EntityFrameworkCore;

namespace LeadFlow.Data;

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
        modelBuilder.Entity<ProcessingLogEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<AvitoAccountEntity>().HasKey(x => x.Id);
    }
}
