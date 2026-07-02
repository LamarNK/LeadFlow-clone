using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

internal sealed class CapturingPanelRealtimeNotifier : IPanelRealtimeNotifier
{
    public IReadOnlyList<PanelChangeKind> LastKinds { get; private set; } = [];
    public Guid? LastOfficeId { get; private set; }
    public Guid? LastWorkerId { get; private set; }
    public int NotifyCount { get; private set; }

    public void Notify(IReadOnlyList<PanelChangeKind> kinds, Guid? officeId = null, Guid? workerId = null)
    {
        NotifyCount++;
        LastKinds = kinds;
        LastOfficeId = officeId;
        LastWorkerId = workerId;
    }
}