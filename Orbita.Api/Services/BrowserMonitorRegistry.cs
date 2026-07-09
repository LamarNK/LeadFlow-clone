using System.Collections.Concurrent;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class BrowserMonitorRegistry
{
    private readonly ConcurrentDictionary<Guid, MonitorRelay> _sessions = new();

    public MonitorRelay GetOrAdd(Guid sessionId) =>
        _sessions.GetOrAdd(sessionId, static id => new MonitorRelay(id));

    public bool TryGet(Guid sessionId, out MonitorRelay? relay) =>
        _sessions.TryGetValue(sessionId, out relay);

    public void Remove(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    public void ClearConnection(string connectionId)
    {
        foreach (var relay in _sessions.Values)
        {
            if (string.Equals(relay.OperatorConnectionId, connectionId, StringComparison.Ordinal))
            {
                relay.OperatorConnectionId = null;
            }

            if (string.Equals(relay.WorkerConnectionId, connectionId, StringComparison.Ordinal))
            {
                relay.WorkerConnectionId = null;
            }
        }
    }

    public sealed class MonitorRelay(Guid sessionId)
    {
        public Guid SessionId { get; } = sessionId;
        public string? OperatorConnectionId { get; set; }
        public string? WorkerConnectionId { get; set; }
        public BrowserMonitorCatalogMessage? LastCatalog { get; set; }
        public readonly ConcurrentDictionary<Guid, BrowserMonitorFrameMessage> LastFrames = new();
    }
}