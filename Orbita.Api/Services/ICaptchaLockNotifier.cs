using Orbita.Contracts;

namespace Orbita.Api.Services;

public interface ICaptchaLockNotifier
{
    Task NotifyLockChangedAsync(Guid workerId, Guid officeId, WorkerCaptchaLockDto lockState, CancellationToken ct = default);
}