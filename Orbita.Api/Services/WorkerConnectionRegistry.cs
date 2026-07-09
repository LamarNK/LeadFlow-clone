using System.Collections.Concurrent;

namespace Orbita.Api.Services;

public sealed class WorkerConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, WorkerConnectionInfo> _byWorker = new();
    private readonly ConcurrentDictionary<string, Guid> _byConnection = new(StringComparer.Ordinal);

    public bool IsConnected(Guid workerId) =>
        _byWorker.ContainsKey(workerId);

    public bool TryGetConnectionId(Guid workerId, out string? connectionId)
    {
        if (_byWorker.TryGetValue(workerId, out var info))
        {
            connectionId = info.ConnectionId;
            return true;
        }

        connectionId = null;
        return false;
    }

    public WorkerConnectionRegistration Register(Guid workerId, string connectionId)
    {
        var displacedConnectionId = (string?)null;
        if (_byWorker.TryGetValue(workerId, out var existing)
            && !string.Equals(existing.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            displacedConnectionId = existing.ConnectionId;
            _byConnection.TryRemove(existing.ConnectionId, out _);
        }

        var info = new WorkerConnectionInfo(workerId, connectionId, DateTime.UtcNow);
        _byWorker[workerId] = info;
        _byConnection[connectionId] = workerId;
        return new WorkerConnectionRegistration(true, displacedConnectionId);
    }

    public bool TryUnregister(string connectionId, out WorkerConnectionInfo? info)
    {
        info = null;
        if (!_byConnection.TryRemove(connectionId, out var workerId))
        {
            return false;
        }

        if (_byWorker.TryGetValue(workerId, out var current)
            && string.Equals(current.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            _byWorker.TryRemove(workerId, out _);
            info = current;
            return true;
        }

        return false;
    }

    public IReadOnlyCollection<Guid> GetConnectedWorkerIds() =>
        _byWorker.Keys.ToArray();
}

public sealed record WorkerConnectionInfo(Guid WorkerId, string ConnectionId, DateTime ConnectedAtUtc);

public sealed record WorkerConnectionRegistration(bool Success, string? DisplacedConnectionId);