using NotifyBot.Domain.Entities;

namespace NotifyBot.Application.Abstractions;

/// <summary>
/// Stores Telegram admin users who can manage routing.
/// </summary>
public interface IAdminUserRepository
{
    Task<AdminUser?> GetByTelegramUserIdAsync(long telegramUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminUser>> GetActiveAdminsAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(AdminUser adminUser, CancellationToken cancellationToken = default);
}