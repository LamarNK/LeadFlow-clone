using Microsoft.AspNetCore.SignalR;
using Orbita.Api.Hubs;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class PanelRealtimeNotifier(
    IHubContext<PanelHub> hub,
    IOrbitaQueryCache queryCache) : IPanelRealtimeNotifier, IDisposable
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(500);

    private readonly object _sync = new();
    private readonly Dictionary<string, PendingNotification> _pending = new();
    private Timer? _flushTimer;

    public void Notify(
        IReadOnlyList<PanelChangeKind> kinds,
        Guid? officeId = null,
        Guid? workerId = null,
        string? operatorMessage = null,
        string? operatorMessageVariant = null)
    {
        if (kinds.Count == 0 && string.IsNullOrWhiteSpace(operatorMessage))
        {
            return;
        }

        // Bump cache versions before the SignalR flush so clients never refresh into
        // a value produced before this write. The cache implementation is explicitly
        // best-effort and falls back to PostgreSQL if Redis is unavailable.
        _ = queryCache.InvalidateAsync(kinds, officeId);

        lock (_sync)
        {
            var key = officeId?.ToString("D") ?? "none";
            if (!_pending.TryGetValue(key, out var entry))
            {
                entry = new PendingNotification(officeId, [], null, null, null);
                _pending[key] = entry;
            }

            foreach (var kind in kinds)
            {
                entry.Kinds.Add(kind);
            }

            if (workerId is not null)
            {
                entry = entry with { WorkerId = workerId };
            }

            if (!string.IsNullOrWhiteSpace(operatorMessage))
            {
                entry = entry with
                {
                    OperatorMessage = operatorMessage,
                    OperatorMessageVariant = operatorMessageVariant ?? "error"
                };
            }

            _pending[key] = entry;
            ScheduleFlushLocked();
        }
    }

    private void ScheduleFlushLocked()
    {
        _flushTimer ??= new Timer(
            static state => ((PanelRealtimeNotifier)state!).FlushAsync(),
            this,
            FlushDelay,
            Timeout.InfiniteTimeSpan);
    }

    private async void FlushAsync()
    {
        List<PendingNotification> snapshot;

        lock (_sync)
        {
            if (_pending.Count == 0)
            {
                _flushTimer?.Dispose();
                _flushTimer = null;
                return;
            }

            snapshot = _pending.Values.ToList();
            _pending.Clear();
            _flushTimer?.Dispose();
            _flushTimer = null;
        }

        var occurredAtUtc = DateTime.UtcNow;

        foreach (var entry in snapshot)
        {
            if (entry.Kinds.Count == 0 && string.IsNullOrWhiteSpace(entry.OperatorMessage))
            {
                continue;
            }

            var notification = new PanelChangeNotification(
                entry.Kinds.OrderBy(static x => x).ToArray(),
                entry.OfficeId,
                entry.WorkerId,
                occurredAtUtc,
                entry.OperatorMessage,
                entry.OperatorMessageVariant);

            if (entry.OfficeId is Guid resolvedOfficeId)
            {
                await hub.Clients
                    .Group(PanelHub.OfficeGroup(resolvedOfficeId))
                    .SendAsync("PanelChanged", notification);
            }

            await hub.Clients
                .Group(PanelHub.GlobalGroup)
                .SendAsync("PanelChanged", notification);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
    }

    private sealed record PendingNotification(
        Guid? OfficeId,
        HashSet<PanelChangeKind> Kinds,
        Guid? WorkerId,
        string? OperatorMessage,
        string? OperatorMessageVariant);
}
