using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

internal sealed class NoopCaptchaSessionRelayNotifier : ICaptchaSessionRelayNotifier
{
    public Task NotifyStateChangedAsync(CaptchaStateChangedMessage message, CancellationToken ct = default) =>
        Task.CompletedTask;
}
