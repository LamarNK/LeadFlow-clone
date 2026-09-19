using LeadFlow.Core.Services.Avito;
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

    [Fact]
    public async Task NormalProfileSwitchWithDormantChallenge_IsNotAnObstacleInAnyProbe()
    {
        await page.SetContentAsync("""
            <div data-marker="osp-sidebar/tools/profile/name">Кадровый отдел</div>
            <div data-marker="component-profile-switch/root" style="position:fixed; inset:20% 30%; background:white; z-index:2">
              <h2>Выбор профиля</h2>
              <div data-marker="component-profile-switch/profile-123">Кадровый отдел</div>
            </div>
            <div class="firewall-container" style="display:none">
              <h2 class="firewall-title">Доступ ограничен: проблема с IP</h2>
              <a href="https://support.avito.ru/request/720">Поддержка</a>
              <div id="geetest_captcha">Продолжить</div>
            </div>
            """);
        await page.EvaluateFunctionAsync("() => { document.title = 'Доступ ограничен: проблема с IP'; }");

        var stateRaw = await page.EvaluateExpressionAsync<string>(AvitoPageStateScripts.BuildProbeScript());
        var state = AvitoPageStateProbe.TryParse(stateRaw);
        Assert.NotNull(state);
        Assert.Equal(AvitoPageKind.ProfileSwitchModal, state.PageKind);
        Assert.False(state.HasCaptcha);
        Assert.False(state.HasFirewallIp);

        var fastRaw = await page.EvaluateExpressionAsync<string>(AvitoCandidatesPageScripts.BuildFirewallProbeScript());
        Assert.Null(AvitoFirewallProbe.TryParse(fastRaw));
        var extractionRaw = await page.EvaluateExpressionAsync<string>(
            AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer());
        using var extraction = System.Text.Json.JsonDocument.Parse(extractionRaw);
        Assert.False(extraction.RootElement.GetProperty("hasCaptcha").GetBoolean());
        Assert.Equal(AvitoPageObstacleKind.None, (await AvitoPageObstacleProbe.ProbeAsync(page, CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task EmptySpaWithStaleBlockTitleAndHash_DoesNotReportIpBlock()
    {
        await page.SetContentAsync("<main></main>");
        await page.EvaluateFunctionAsync("() => { document.title = 'Доступ ограничен: проблема с IP'; location.hash = '#block'; }");

        var state = AvitoPageStateProbe.TryParse(
            await page.EvaluateExpressionAsync<string>(AvitoPageStateScripts.BuildProbeScript()));
        Assert.NotNull(state);
        Assert.False(state.HasCaptcha);
        Assert.False(state.HasFirewallIp);

        var fastRaw = await page.EvaluateExpressionAsync<string>(AvitoCandidatesPageScripts.BuildFirewallProbeScript());
        Assert.Null(AvitoFirewallProbe.TryParse(fastRaw));

        var readyRaw = await page.EvaluateExpressionAsync<string>(AvitoCandidatesPageScripts.BuildWaitForReadyProbeScript());
        using var ready = System.Text.Json.JsonDocument.Parse(readyRaw);
        Assert.False(ready.RootElement.GetProperty("blocked").GetBoolean());
    }

    [Fact]
    public async Task VisibleFirewallAndCaptcha_AreStillReportedByBothProbes()
    {
        await page.SetContentAsync("""
            <div class="firewall-container">
              <h2 class="firewall-title">Доступ ограничен: проблема с IP</h2>
              <p>Отключить VPN</p>
              <a href="https://support.avito.ru/request/720">Поддержка</a>
            </div>
            """);
        var ipState = AvitoPageStateProbe.TryParse(
            await page.EvaluateExpressionAsync<string>(AvitoPageStateScripts.BuildProbeScript()));
        Assert.NotNull(ipState);
        Assert.True(ipState.HasFirewallIp);
        Assert.Equal("firewall", AvitoFirewallProbe.TryParse(
            await page.EvaluateExpressionAsync<string>(AvitoCandidatesPageScripts.BuildFirewallProbeScript()))?.Kind);

        await page.SetContentAsync("""
            <div role="dialog" aria-modal="true" style="position:fixed; inset:20%; background:white">
              <h2>Решение капчи</h2><div id="geetest_captcha">Продолжить</div>
            </div>
            """);
        var captchaState = AvitoPageStateProbe.TryParse(
            await page.EvaluateExpressionAsync<string>(AvitoPageStateScripts.BuildProbeScript()));
        Assert.NotNull(captchaState);
        Assert.True(captchaState.HasCaptcha);
        Assert.False(captchaState.HasFirewallIp);
        Assert.Equal("geetest", AvitoFirewallProbe.TryParse(
            await page.EvaluateExpressionAsync<string>(AvitoCandidatesPageScripts.BuildFirewallProbeScript()))?.Kind);
    }

    [Fact]
    public async Task CoveredGeeTestWidget_DoesNotHideProfileSwitch()
    {
        await page.SetContentAsync("""
            <div id="geetest_captcha" style="position:fixed; inset:0; z-index:1; background:white">Продолжить</div>
            <div data-marker="component-profile-switch/root" style="position:fixed; inset:10%; z-index:2; background:white">
              <h2>Выбор профиля</h2>
            </div>
            """);

        var state = AvitoPageStateProbe.TryParse(
            await page.EvaluateExpressionAsync<string>(AvitoPageStateScripts.BuildProbeScript()));
        Assert.NotNull(state);
        Assert.Equal(AvitoPageKind.ProfileSwitchModal, state.PageKind);
        Assert.Null(AvitoFirewallProbe.TryParse(
            await page.EvaluateExpressionAsync<string>(AvitoCandidatesPageScripts.BuildFirewallProbeScript())));
    }

    [Fact]
    public async Task DisplayContentsProfileSwitch_SuppressesUnderlyingFirewall()
    {
        await page.SetContentAsync("""
            <div class="firewall-container" style="position:fixed; inset:0; z-index:1; background:white">
              <h2 class="firewall-title">Доступ ограничен: проблема с IP</h2>
              <a href="https://support.avito.ru/request/720">Поддержка</a>
            </div>
            <div data-marker="component-profile-switch/root" style="display:contents">
              <div style="position:fixed; inset:10%; z-index:2; background:white">
                <h2>Выбор профиля</h2>
                <div data-marker="component-profile-switch/profile-123">Кадровый отдел</div>
              </div>
            </div>
            """);

        Assert.Equal(
            AvitoPageObstacleKind.None,
            (await AvitoPageObstacleProbe.ProbeAsync(page, CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task PortaledProfileSwitch_WithStaleFirewallTitle_IsNotAnObstacle()
    {
        // Production 1.0.3.27 still reported «Выбор профиля» as IP-block/captcha:
        // the modal is a portal sibling of an empty display:contents root, the
        // heading is not h1/h2/h3, leftover firewall copy stays in the DOM, and
        // the tab title/hash can remain from a previous #block page.
        await page.SetContentAsync("""
            <div class="firewall-container" style="position:fixed; inset:0; z-index:1">
              <h2 class="firewall-title">Доступ ограничен: проблема с IP</h2>
              <p>нажмите на кнопку Продолжить для решения капчи</p>
              <button>Продолжить</button>
              <a href="https://support.avito.ru/request/720">Поддержка</a>
              <script src="https://static.geetest.com/v4/gt4.js"></script>
            </div>
            <div data-marker="component-profile-switch/root" style="display:contents"></div>
            <div role="dialog" aria-modal="true" style="position:fixed; inset:12% 32%; z-index:2; background:white">
              <div>Выбор профиля</div>
              <input placeholder="Название или категория">
              <div data-marker="component-profile-switch/profile-123"><h5>Кадровый отдел Рязань 5</h5></div>
              <div data-marker="component-profile-switch/profile-124"><h5>Кадровый отдел Рязань 4</h5></div>
            </div>
            """);
        await page.EvaluateFunctionAsync("""
            () => {
                document.title = 'Доступ ограничен: проблема с IP';
                location.hash = '#profile/switch?withEntities=true';
            }
            """);

        var obstacle = await AvitoPageObstacleProbe.ProbeAsync(page, CancellationToken.None);
        Assert.Equal(AvitoPageObstacleKind.None, obstacle.Kind);

        var state = AvitoPageStateProbe.TryParse(
            await page.EvaluateExpressionAsync<string>(AvitoPageStateScripts.BuildProbeScript()));
        Assert.NotNull(state);
        Assert.False(state.HasCaptcha);
        Assert.False(state.HasFirewallIp);
        Assert.Null(AvitoFirewallProbe.TryParse(
            await page.EvaluateExpressionAsync<string>(AvitoCandidatesPageScripts.BuildFirewallProbeScript())));
    }

    [Fact]
    public async Task GeeTestOverPortaledProfileSwitch_IsStillCaptcha()
    {
        await page.SetContentAsync("""
            <div data-marker="component-profile-switch/root" style="display:contents"></div>
            <div role="dialog" aria-modal="true" style="position:fixed; inset:12% 32%; z-index:1; background:white">
              <div>Выбор профиля</div>
              <div data-marker="component-profile-switch/profile-123"><h5>Кадровый отдел</h5></div>
            </div>
            <div class="geetest_boxShow" style="position:fixed; inset:20% 35%; z-index:3; background:white">
              Переместите слайдером деталь, чтобы сложить пазл
            </div>
            """);

        Assert.Equal(
            AvitoPageObstacleKind.Captcha,
            (await AvitoPageObstacleProbe.ProbeAsync(page, CancellationToken.None)).Kind);
    }
}
