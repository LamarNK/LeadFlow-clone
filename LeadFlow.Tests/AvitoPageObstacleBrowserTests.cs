using LeadFlow.Core.Services.Avito.Session;
using PuppeteerSharp;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoPageObstacleBrowserTests : IAsyncLifetime
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
    }

    public async Task DisposeAsync() => await browser.DisposeAsync();

    [Fact]
    public async Task HiddenFirewallBehindProfileSwitch_IsNotIpBlocked()
    {
        await page.SetContentAsync("""
            <div data-marker="component-profile-switch/root">
              <h2>Выбор профиля</h2>
              <div data-marker="component-profile-switch/profile-440795296">Кадровый отдел</div>
            </div>
            <div class="firewall-container" style="display:none">
              <h2 class="firewall-title">Доступ ограничен: проблема с IP</h2>
              <a href="https://support.avito.ru/request/720">Поддержка</a>
            </div>
            """);
        await page.EvaluateFunctionAsync("() => { document.title = 'Доступ ограничен: проблема с IP'; }");

        Assert.Equal(AvitoPageObstacleKind.None, (await AvitoPageObstacleProbe.ProbeAsync(page, CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task VisibleFirewallOverProfileSwitch_IsIpBlocked()
    {
        await page.SetContentAsync("""
            <div data-marker="component-profile-switch/root" style="display:none">
              <div data-marker="component-profile-switch/profile-440795296">Кадровый отдел</div>
            </div>
            <div data-marker="job-application/item">Фоновый отклик</div>
            <div class="firewall-container">
              <h2 class="firewall-title">Доступ ограничен: проблема с IP</h2>
              <p>Отключить VPN</p>
              <a href="https://support.avito.ru/request/720">Поддержка</a>
            </div>
            """);

        Assert.Equal(AvitoPageObstacleKind.IpBlocked, (await AvitoPageObstacleProbe.ProbeAsync(page, CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task FirewallCoveredByProfileSwitch_IsNotIpBlocked()
    {
        await page.SetContentAsync("""
            <div class="firewall-container" style="position:fixed; inset:0; z-index:1; display:flex; align-items:center; justify-content:center">
              <div>
                <h2 class="firewall-title">Доступ ограничен: проблема с IP</h2>
                <a href="https://support.avito.ru/request/720">Поддержка</a>
              </div>
            </div>
            <div data-marker="component-profile-switch/root" style="position:fixed; inset:18% 30%; z-index:2; background:white">
              <h2>Выбор профиля</h2>
              <div data-marker="component-profile-switch/profile-440795296">Кадровый отдел</div>
            </div>
            """);
        await page.EvaluateFunctionAsync("() => { location.hash = '#block'; }");

        Assert.Equal(AvitoPageObstacleKind.None, (await AvitoPageObstacleProbe.ProbeAsync(page, CancellationToken.None)).Kind);
    }
}
