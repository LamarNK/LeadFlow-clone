using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class WorkerUpdateOfferSource
{
    private readonly Lock _sync = new();
    private WorkerUpdateOfferDto? _current;

    public WorkerUpdateOfferDto? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void SetOffer(WorkerUpdateOfferDto? offer)
    {
        lock (_sync)
        {
            _current = offer;
        }
    }
}