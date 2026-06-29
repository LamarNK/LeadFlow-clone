using PuppeteerSharp;

namespace LeadFlow.Core.Services.Browser;

public static class BrowserDiagnosticsCapture
{
    public static async Task<byte[]?> CapturePageScreenshotAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
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
}