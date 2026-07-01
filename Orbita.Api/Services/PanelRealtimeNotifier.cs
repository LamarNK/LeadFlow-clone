using Microsoft.AspNetCore.SignalR;
using Orbita.Api.Hubs;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class PanelRealtimeNotifier(IHubContext<PanelHub> hub) : IPanelRealtimeNotifier, IDisposable
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(500);

    private readonly object _sync = new();
    private readonly Dictionary<string, (Guid? OfficeId, HashSet<PanelChangeKind> Kinds)> _pending = new();
    private Guid? _workerId;
    private Timer? _flushTimer;

    public void Notify(
        IReadOnlyList<PanelChangeKind> kinds,
        Guid? officeId = null,
        Guid? workerId = null)
    {
        if (kinds.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            var key = officeId?.ToString("D") ?? "none";
            if (!_pending.TryGetValue(key, out var entry))
            {
                entry = (officeId, []);
                _pending[key] = entry;
            }

            foreach (var kind in kinds)
            {
                entry.Kinds.Add(kind);
            }

            _pending[key] = (officeId, entry.Kinds);

            if (workerId is not null)
            {
                _workerId = workerId;
            }

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
        List<(Guid? OfficeId, HashSet<PanelChangeKind> Kinds)> snapshot;
        Guid? workerId;

        lock (_sync)
        {
            if (_pending.Count == 0)
            {
                _flushTimer?.Dispose();
                _flushTimer = null;
                return;
            }

            snapshot = _pending.Values
                .Select(static x => (x.OfficeId, x.Kinds.ToHashSet()))
                .ToList();
            workerId = _workerId;
            _pending.Clear();
            _workerId = null;
            _flushTimer?.Dispose();
            _flushTimer = null;
        }

        var occurredAtUtc = DateTime.UtcNow;

        foreach (var (officeId, kinds) in snapshot)
        {
            if (kinds.Count == 0)
            {
                continue;
            }

            var notification = new PanelChangeNotification(
                kinds.OrderBy(static x => x).ToArray(),
                officeId,
                workerId,
                occurredAtUtc);

            if (officeId is Guid resolvedOfficeId)
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
}