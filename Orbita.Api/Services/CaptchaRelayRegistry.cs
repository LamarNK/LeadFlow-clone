using System.Collections.Concurrent;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CaptchaRelayRegistry
{
    private readonly ConcurrentDictionary<Guid, RelaySession> _sessions = new();

    public RelaySession GetOrAdd(Guid sessionId) =>
        _sessions.GetOrAdd(sessionId, static id => new RelaySession(id));

    public bool TryGet(Guid sessionId, out RelaySession? session) =>
        _sessions.TryGetValue(sessionId, out session);

    public void Remove(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    public sealed class RelaySession(Guid sessionId)
    {
        public Guid SessionId { get; } = sessionId;
        public string? OperatorConnectionId { get; set; }
        public string? WorkerConnectionId { get; set; }
        public CaptchaFrameMessage? LastFrame { get; set; }
        public CaptchaSnapshotMessage? LastSnapshot { get; set; }
        public int FramesWithoutOperatorLogged { get; set; }
        public int SnapshotsWithoutOperatorLogged { get; set; }
        public readonly ConcurrentQueue<CaptchaInputEnvelope> PendingInputs = new();
    }

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
}

public sealed record CaptchaInputEnvelope(
    string EventType,
    double X,
    double Y,
    long TimestampMs,
    int Button,
    int Buttons,
    string? Key,
    string? Code,
    bool AltKey,
    bool CtrlKey,
    bool ShiftKey,
    bool MetaKey,
    bool Repeat);
