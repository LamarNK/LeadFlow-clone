using PuppeteerSharp;

namespace LeadFlow.Core.Services.Captcha;

public interface IAvitoGeeTestSolver
{
    Task<bool> TrySolveOnPageAsync(
        IPage page,
        string? html,
        string? pageUrl,
        CancellationToken cancellationToken = default);
}
