using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerCommandService(
    OrbitaDbContext db,
    OfficeScopeService officeScope,
    IWorkerPushNotifier pushNotifier)
{
    private static readonly HashSet<string> AllowedCommands =
        new(StringComparer.OrdinalIgnoreCase) { WorkerCommands.Restart };

    public async Task<(bool Success, string? Error)> EnqueueAsync(
        Guid workerId,
        string command,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command) || !AllowedCommands.Contains(command.Trim()))
        {
            return (false, "Неизвестная команда.");
        }

        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return (false, "Воркер не найден.");
        }

        var worker = await db.Workers.FirstOrDefaultAsync(x => x.Id == workerId, ct);
        if (worker is null)
        {
            return (false, "Воркер не найден.");
        }

        var normalized = command.Trim().ToLowerInvariant();
        worker.PendingCommand = normalized;
        worker.PendingCommandAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await pushNotifier.TryPushCommandAsync(workerId, normalized, ct).ConfigureAwait(false);
        return (true, null);
    }
}