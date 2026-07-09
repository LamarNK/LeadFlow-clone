using System.IO.Compression;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Captcha;

public static class CaptchaScreenshotCapture
{
    public static async Task<string?> CaptureJpegGzipBase64Async(
        IPage page,
        int quality = 72,
        CancellationToken cancellationToken = default)
    {
        var bytes = await page.ScreenshotDataAsync(new ScreenshotOptions
        {
            Type = ScreenshotType.Jpeg,
            Quality = quality,
            FullPage = false
        }).ConfigureAwait(false);

        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        await using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        return Convert.ToBase64String(output.ToArray());
    }
}