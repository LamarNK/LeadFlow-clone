using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace NotifyBot.Infrastructure.Data;

public static class DatabaseInitializer
{
    public static async Task MigrateAndSeedAsync(
        NotifyBotDbContext dbContext,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);

        if (!await dbContext.Cards.AnyAsync(cancellationToken))
        {
            dbContext.Cards.AddRange(CardSeedData.Cards);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded {Count} default cards", CardSeedData.Cards.Length);
        }
    }
}