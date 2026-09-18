using System.Reflection;
using LeadFlow.Core.Services.Captcha;
using PuppeteerSharp;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoGeeTestCaptureBrowserTests : IAsyncLifetime
{
    private IBrowser browser = null!;
    private IPage page = null!;

    public async Task InitializeAsync()
    {
        var executable = Environment.GetEnvironmentVariable("AVITO_TEST_CHROME");
        if (string.IsNullOrWhiteSpace(executable))
        {
            executable = new[]
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                "/usr/bin/chromium", "/usr/bin/google-chrome"
            }.FirstOrDefault(File.Exists);
        }

        Assert.True(File.Exists(executable), "Set AVITO_TEST_CHROME to an installed Chromium executable.");
        browser = await Puppeteer.LaunchAsync(new LaunchOptions { ExecutablePath = executable, Headless = true });
        page = await browser.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 1200, Height = 800, DeviceScaleFactor = 1.25 });
    }

    public async Task DisposeAsync() => await browser.DisposeAsync();

    [Fact]
    public async Task ClickCapture_UsesVisibleHintInsteadOfReconstructedCanvas()
    {
        await page.SetContentAsync($$"""
            <style>
              html,body { margin:0; background:#243447; }
              .geetest_ques_tips { display:inline-flex; gap:4px; padding:8px; background:#00ff00; }
            </style>
            <div class="geetest_box">
              <div class="geetest_text_tips">Нажмите на элементы в указанном порядке:</div>
              <div class="geetest_ques_tips"><img><img><img></div>
              <div class="geetest_click"><div class="geetest_bg" style="width:303px;height:202px"></div></div>
            </div>
            """);
        await page.EvaluateFunctionAsync(
            """
            async () => {
              const png = (width, height, paint) => {
                const canvas = document.createElement('canvas');
                canvas.width = width;
                canvas.height = height;
                const context = canvas.getContext('2d');
                paint(context);
                return canvas.toDataURL('image/png');
              };
              const main = png(303, 202, context => {
                context.fillStyle = '#6699cc';
                context.fillRect(0, 0, 303, 202);
              });
              const hint = png(120, 120, context => {
                context.fillStyle = 'black';
                context.beginPath();
                context.arc(60, 60, 30, 0, Math.PI * 2);
                context.fill();
              });
              document.querySelector('.geetest_bg').style.backgroundImage = `url("${main}")`;
              const images = Array.from(document.querySelectorAll('.geetest_ques_tips img'));
              images.forEach(image => image.src = hint);
              await Promise.all(images.map(image => image.decode()));
            }
            """);

        var method = typeof(AvitoGeeTestSolver).GetMethod(
            "CaptureLoginClickCaptchaAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(null, [page, CancellationToken.None]));
        await task;
        var capture = task.GetType().GetProperty("Result")?.GetValue(task);
        Assert.NotNull(capture);
        var hintPng = Assert.IsType<string>(capture.GetType().GetProperty("HintImageBody")?.GetValue(capture));

        var corner = await page.EvaluateFunctionAsync<int[]>(
            """
            async png => {
              const image = new Image();
              image.src = `data:image/png;base64,${png}`;
              await image.decode();
              const canvas = document.createElement('canvas');
              canvas.width = image.naturalWidth;
              canvas.height = image.naturalHeight;
              const context = canvas.getContext('2d');
              context.drawImage(image, 0, 0);
              return Array.from(context.getImageData(canvas.width - 2, canvas.height - 2, 1, 1).data);
            }
            """,
            hintPng);

        Assert.Equal([0, 255, 0, 255], corner);
    }
}
