using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

internal sealed class NoopPanelRealtimeNotifier : IPanelRealtimeNotifier
{
    public void Notify(IReadOnlyList<PanelChangeKind> kinds, Guid? officeId = null, Guid? workerId = null)
    {
    }
}