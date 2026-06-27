using Microsoft.EntityFrameworkCore;
using NotifyBot.Application.Abstractions;
using NotifyBot.Domain.Entities;

namespace NotifyBot.Infrastructure.Data;

public sealed class AdminUserRepository(NotifyBotDbContext dbContext) : IAdminUserRepository
{
    public Task<AdminUser?> GetByTelegramUserIdAsync(long telegramUserId, CancellationToken cancellationToken = default) =>
        dbContext.AdminUsers.AsNoTracking().FirstOrDefaultAsync(x => x.TelegramUserId == telegramUserId, cancellationToken);

    public async Task<IReadOnlyList<AdminUser>> GetActiveAdminsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.AdminUsers
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Username)
            .ToListAsync(cancellationToken);

    public async Task UpsertAsync(AdminUser adminUser, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.AdminUsers.FirstOrDefaultAsync(
            x => x.TelegramUserId == adminUser.TelegramUserId,
            cancellationToken);

        if (existing is null)
        {
            dbContext.AdminUsers.Add(adminUser);
        }
        else
        {
            existing.Username = adminUser.Username;
            existing.IsActive = adminUser.IsActive;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}