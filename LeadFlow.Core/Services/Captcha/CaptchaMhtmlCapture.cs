using System.IO.Compression;
using System.Text;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Captcha;

public static class CaptchaMhtmlCapture
{
    public static async Task<string?> CaptureGzipBase64Async(IPage page, CancellationToken cancellationToken = default)
    {
        var mhtml = await CaptureMhtmlAsync(page, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(mhtml))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(mhtml);
        await using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    public static async Task<string?> CaptureMhtmlAsync(IPage page, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await page.Client
                .SendAsync("Page.captureSnapshot", new { format = "mhtml" })
                .ConfigureAwait(false);

            return response.Value.GetProperty("data").GetString();
        }
        catch
        {
            return null;
        }
    }
}