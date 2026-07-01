using Orbita.Contracts;

namespace Orbita.Api.Services;

public interface IPanelRealtimeNotifier
{
    void Notify(
        IReadOnlyList<PanelChangeKind> kinds,
        Guid? officeId = null,
        Guid? workerId = null);
}