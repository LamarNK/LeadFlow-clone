using PuppeteerSharp;

namespace LeadFlow.Core.Services.Browser;

public static class BrowserDiagnosticsCapture
{
    public static async Task<byte[]?> CapturePageScreenshotAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await WaitForRenderableContentAsync(page, cancellationToken).ConfigureAwait(false);
            return await page.ScreenshotDataAsync(new ScreenshotOptions
            {
                FullPage = false,
                Type = ScreenshotType.Png
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WaitForRenderableContentAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await page.WaitForFunctionAsync(
                    """
                    () => {
                        const bodyLen = (document.body?.innerText ?? '').trim().length;
                        if (bodyLen > 40) return true;
                        if (document.querySelector("[data-marker='login-form'], [data-marker='auth-app-root']")) return true;
                        if (document.querySelector("[data-marker='job-application/item']")) return true;
                        return document.readyState === 'complete' && bodyLen > 0;
                    }
                    """,
                    new WaitForFunctionOptions
                    {
                        Timeout = 4_000,
                        PollingInterval = 250
                    })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Screenshot is best-effort even on a blank page.
        }
    }
}