using PuppeteerSharp;

namespace LeadFlow.Core.Services.Captcha;

public interface IAvitoGeeTestSolver
{
    Task<bool> TrySolveOnPageAsync(
        IPage page,
        string? html,
        string? pageUrl,
        CancellationToken cancellationToken = default);

    Task<bool> TrySolveOnPageAsync(
        IPage page,
        string? html,
        string? pageUrl,
        GeeTestV4TaskOptions? taskOptions,
        CancellationToken cancellationToken = default);
}
