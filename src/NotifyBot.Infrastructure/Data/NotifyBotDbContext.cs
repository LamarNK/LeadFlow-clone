using Microsoft.EntityFrameworkCore;
using NotifyBot.Domain.Entities;
using NotifyBot.Domain.Enums;

namespace NotifyBot.Infrastructure.Data;

public sealed class NotifyBotDbContext(DbContextOptions<NotifyBotDbContext> options) : DbContext(options)
{
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<TelegramChat> TelegramChats => Set<TelegramChat>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Card>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Last4).HasMaxLength(4).IsRequired();
            entity.Property(x => x.Label).HasMaxLength(100);
            entity.HasIndex(x => x.Last4).IsUnique();
        });

        modelBuilder.Entity<TelegramChat>(entity =>
        {
            entity.HasKey(x => x.ChatId);
            entity.Property(x => x.ChatType).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Title).HasMaxLength(256);
            entity.Property(x => x.Username).HasMaxLength(64);
            entity.HasIndex(x => x.IsActive);
        });

        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.HasKey(x => x.TelegramUserId);
            entity.Property(x => x.Username).HasMaxLength(64);
            entity.HasIndex(x => x.IsActive);
        });
    }
}