using System.Text.Json;
using LeadFlow.Core.Services.Avito;
using PuppeteerSharp;
using Xunit;

namespace LeadFlow.Tests;

// Executes the production scripts in Chromium. No Avito account or network is used.
public sealed class AvitoPhoneOwnershipBrowserTests : IAsyncLifetime
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

    private static string Card(string name, string vacancy = "1234567890") => $$"""
        <div data-marker="job-application/item"><h3>{{name}}</h3>
        <a data-marker="job-application/link/to-resume" href="https://www.avito.ru/{{vacancy}}">Оператор · Пермь · сегодня, 10:00</a>
        <button data-marker="job-application/response/status-select-button">Новый кандидат</button>
        <button data-marker="job-application/call-button" aria-label="Показать номер телефона"></button></div>
        """;

    private static string Popup(string name, string phone = "8 900 123-45-67", string style = "") => $$"""
        <div data-marker="job-application/response/contacts-popup/popup" style="{{style}}">
        <h1>{{name}}</h1><p>Временный номер</p><h3>{{phone}}</h3></div>
        """;

    private async Task<JsonElement> Run(string script) =>
        JsonDocument.Parse(await page.EvaluateExpressionAsync<string>(script)).RootElement.Clone();

    private async Task SelectFirstAsync()
    {
        await Run(AvitoCandidatesPageScripts.BuildResetCandidateCollectionStateScript());
        var target = await Run(AvitoCandidatesPageScripts.BuildFindContactsPopupTargetScript());
        Assert.Equal(0, target.GetProperty("targetIndex").GetInt32());
    }

    [Fact]
    public async Task PopupForAnotherCandidate_IsNotCached()
    {
        await page.SetContentAsync(Card("Анна Иванова") + Card("Ольга Петрова"));
        await SelectFirstAsync();
        await page.EvaluateFunctionAsync("html => document.body.insertAdjacentHTML('beforeend', html)", Popup("Ольга Петрова"));
        var result = await Run(AvitoCandidatesPageScripts.BuildContactsPopupProbeScript(0));
        Assert.False(result.GetProperty("revealed").GetBoolean());
    }

    [Fact]
    public async Task ReorderedCardDuringReveal_IsNotCachedUnderOldIndex()
    {
        await page.SetContentAsync(Card("Анна Иванова") + Card("Ольга Петрова"));
        await SelectFirstAsync();
        await page.EvaluateFunctionAsync("html => { document.body.prepend(document.querySelectorAll('[data-marker=\"job-application/item\"]')[1]); document.body.insertAdjacentHTML('beforeend', html); }", Popup("Анна Иванова"));
        var result = await Run(AvitoCandidatesPageScripts.BuildContactsPopupProbeScript(0));
        Assert.False(result.GetProperty("revealed").GetBoolean());
    }

    [Fact]
    public async Task HiddenOldPopup_DoesNotOverrideVisiblePopup()
    {
        await page.SetContentAsync(Card("Анна Иванова"));
        await SelectFirstAsync();
        await page.EvaluateFunctionAsync("html => document.body.insertAdjacentHTML('beforeend', html)",
            Popup("Ольга Петрова", "8 900 999-99-99", "display:none") + Popup("Анна Иванова"));
        var result = await Run(AvitoCandidatesPageScripts.BuildContactsPopupProbeScript(0));
        Assert.True(result.GetProperty("revealed").GetBoolean());
        Assert.Equal("8 900 123-45-67", result.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task SimpleCards_AreNotPanelTargets()
    {
        await page.SetContentAsync("<div data-marker='filters/status-list-content'></div>" + Card("Анна Иванова"));
        await Run(AvitoCandidatesPageScripts.BuildResetCandidateCollectionStateScript());
        var result = await Run(AvitoCandidatesPageScripts.BuildFindPanelPhoneTargetScript());
        Assert.Equal(-1, result.GetProperty("targetIndex").GetInt32());
    }

    [Fact]
    public async Task PanelCallOpensOwnedPopup_ExtractionUsesItsPhone()
    {
        await page.SetContentAsync(Card("Анна Иванова").Replace("<div data-marker", "<div role='button' data-marker")
            + "<aside><div>Анна Иванова</div><button data-marker='job-application/call-button'>Показать номер</button></aside>");
        await Run(AvitoCandidatesPageScripts.BuildResetCandidateCollectionStateScript());
        await Run(AvitoCandidatesPageScripts.BuildFindPanelPhoneTargetScript());
        await page.EvaluateFunctionAsync("html => document.querySelector('aside button').addEventListener('click', () => document.body.insertAdjacentHTML('beforeend', html))", Popup("Анна Иванова"));
        await Run(AvitoCandidatesPageScripts.BuildRevealPanelPhoneScript());
        var result = await Run(AvitoCandidatesPageScripts.BuildCandidatePanelPhoneProbeScript(0, "\"Анна Иванова\""));
        Assert.True(result.GetProperty("revealed").GetBoolean());
        var extraction = await Run(AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer());
        var candidate = Assert.Single(extraction.GetProperty("candidates").EnumerateArray());
        Assert.Equal("Анна Иванова", candidate.GetProperty("fullName").GetString());
        Assert.Equal("8 900 123-45-67", candidate.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task SuccessfulCache_IsNotTransferredWhenCardsReorder()
    {
        await page.SetContentAsync(Card("Анна Иванова") + Card("Ольга Петрова"));
        await SelectFirstAsync();
        await page.EvaluateFunctionAsync("html => document.body.insertAdjacentHTML('beforeend', html)", Popup("Анна Иванова"));
        Assert.True((await Run(AvitoCandidatesPageScripts.BuildContactsPopupProbeScript(0))).GetProperty("revealed").GetBoolean());
        await page.EvaluateExpressionAsync("document.body.prepend(document.querySelectorAll('[data-marker=\"job-application/item\"]')[1])");
        var extraction = await Run(AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer());
        Assert.Empty(extraction.GetProperty("candidates").EnumerateArray());
    }

    [Theory]
    [InlineData("display:none")]
    [InlineData("visibility:hidden")]
    public async Task HiddenPanel_IsNeverClicked(string style)
    {
        await page.SetContentAsync(Card("Анна Иванова").Replace("<div data-marker", "<div role='button' data-marker")
            + $"<aside style='{style}'><div>Анна Иванова</div><button data-marker='job-application/call-button'>Показать номер</button></aside>");
        await Run(AvitoCandidatesPageScripts.BuildFindPanelPhoneTargetScript());
        var result = await Run(AvitoCandidatesPageScripts.BuildRevealPanelPhoneScript());
        Assert.False(result.GetProperty("clicked").GetBoolean());
    }

    [Fact]
    public async Task OldPanelWithDifferentName_IsNeverClicked()
    {
        await page.SetContentAsync(Card("Анна Иванова").Replace("<div data-marker", "<div role='button' data-marker")
            + "<aside><div>Ольга Петрова</div><button data-marker='job-application/call-button'>Показать номер</button></aside>");
        await Run(AvitoCandidatesPageScripts.BuildFindPanelPhoneTargetScript());
        Assert.False((await Run(AvitoCandidatesPageScripts.BuildRevealPanelPhoneScript())).GetProperty("clicked").GetBoolean());
        Assert.False((await Run(AvitoCandidatesPageScripts.BuildCandidatePanelPhoneProbeScript(0, "\"Анна Иванова\""))).GetProperty("revealed").GetBoolean());
    }

    [Fact]
    public async Task CandidateReusedInSameNodeForAnotherVacancy_IsRejected()
    {
        await page.SetContentAsync(Card("Анна Иванова"));
        await SelectFirstAsync();
        await page.EvaluateFunctionAsync("html => { document.querySelector('a').href='https://www.avito.ru/9876543210'; document.body.insertAdjacentHTML('beforeend', html); }", Popup("Анна Иванова"));
        Assert.False((await Run(AvitoCandidatesPageScripts.BuildContactsPopupProbeScript(0))).GetProperty("revealed").GetBoolean());
    }

    [Fact]
    public async Task RepeatPassAndReload_DoNotReuseOldPopupOrCache()
    {
        await page.SetContentAsync(Card("Анна Иванова"));
        await SelectFirstAsync();
        await page.EvaluateFunctionAsync("html => document.body.insertAdjacentHTML('beforeend', html)", Popup("Анна Иванова"));
        Assert.True((await Run(AvitoCandidatesPageScripts.BuildContactsPopupProbeScript(0))).GetProperty("revealed").GetBoolean());
        var first = await Run(AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer());
        var repeat = await Run(AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer());
        Assert.Equal(first.GetProperty("candidates").GetRawText(), repeat.GetProperty("candidates").GetRawText());
        await Run(AvitoCandidatesPageScripts.BuildResetCandidateCollectionStateScript());
        Assert.True((await Run(AvitoCandidatesPageScripts.BuildFindContactsPopupTargetScript())).GetProperty("closedExisting").GetBoolean());
        Assert.False((await Run(AvitoCandidatesPageScripts.BuildContactsPopupProbeScript(0))).GetProperty("revealed").GetBoolean());
        await page.ReloadAsync();
        await page.SetContentAsync(Card("Анна Иванова"));
        Assert.Empty((await Run(AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer())).GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task FailedContactsAndMissingPhone_AreNotExtracted()
    {
        await page.SetContentAsync(Card("Анна Иванова"));
        await SelectFirstAsync();
        await page.EvaluateFunctionAsync("html => document.body.insertAdjacentHTML('beforeend', html)", Popup("Анна Иванова", "Не удалось загрузить контактные данные"));
        var probe = await Run(AvitoCandidatesPageScripts.BuildContactsPopupProbeScript(0));
        Assert.False(probe.GetProperty("revealed").GetBoolean());
        Assert.Equal("error", probe.GetProperty("state").GetString());
        Assert.Empty((await Run(AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer())).GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task PrepareAsync_WaitsForPanelThenPopup_WithinSharedBudget()
    {
        var html = Card("Анна Иванова").Replace("<div data-marker", "<div role='button' data-marker")
            .Replace("<button data-marker=\"job-application/call-button\" aria-label=\"Показать номер телефона\"></button>", "");
        await page.SetContentAsync(html);
        await page.EvaluateFunctionAsync("""
            popup => document.querySelector('[data-marker="job-application/item"]').addEventListener('click', () => {
                setTimeout(() => {
                    const panel = document.createElement('aside');
                    panel.innerHTML = '<div>Анна Иванова</div><button data-marker="job-application/call-button">Показать номер</button>';
                    panel.querySelector('button').addEventListener('click', () => setTimeout(() => document.body.insertAdjacentHTML('beforeend', popup), 250));
                    document.body.append(panel);
                }, 250);
            })
            """, Popup("Анна Иванова"));
        var budget = AvitoAccountPassBudget.ForAccountPass();
        budget.ReservePhoneRevealClicks(budget.PhoneRevealClicksCap - 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await AvitoCandidatesListPreparer.PrepareAsync(
            async (script, ct) => { ct.ThrowIfCancellationRequested(); return await page.EvaluateExpressionAsync<string>(script); },
            "browser-test", timeout.Token, skipDetailEnrich: true, passBudget: budget);
        Assert.Equal(1, result.PanelPhoneSuccesses);
        Assert.Equal(1, result.PhoneRevealClicks);
        Assert.Equal(0, budget.PhoneRevealClicksRemaining);
        Assert.Single((await Run(AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer())).GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task PrepareAsync_ExhaustedBudget_DoesNotOpenPanel()
    {
        await page.SetContentAsync(Card("Анна Иванова").Replace("<div data-marker", "<div role='button' data-marker"));
        await page.EvaluateExpressionAsync("window.testClicks=0; document.body.addEventListener('click',()=>window.testClicks++)");
        var budget = AvitoAccountPassBudget.ForAccountPass();
        budget.ReservePhoneRevealClicks(budget.PhoneRevealClicksCap);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await AvitoCandidatesListPreparer.PrepareAsync(
            async (script, ct) => { ct.ThrowIfCancellationRequested(); return await page.EvaluateExpressionAsync<string>(script); },
            "browser-test", timeout.Token, skipDetailEnrich: true, passBudget: budget);
        Assert.Equal(0, result.PanelPhoneClicks);
        Assert.Equal(0, result.PhoneRevealBudget);
        Assert.Equal(0, await page.EvaluateExpressionAsync<int>("window.testClicks"));
    }

    [Fact]
    public async Task SkippedCard_IsNotReportedAsHavingPhone()
    {
        await page.SetContentAsync(Card("Анна Иванова"));
        await Run(AvitoCandidatesPageScripts.BuildApplyPhoneRevealSkipScript(new[] { 0 }));
        var probe = await Run(AvitoCandidatesPageScripts.BuildPhonesReadyProbeScript());
        Assert.Equal(0, probe.GetProperty("withPhone").GetInt32());
        Assert.Equal(1, probe.GetProperty("excluded").GetInt32());
        Assert.Equal(0, probe.GetProperty("masked").GetInt32());
    }

    [Fact]
    public async Task DetailReadWithoutConfirmedTarget_RejectsGlobalPhone()
    {
        await page.SetContentAsync(Card("Ольга Петрова").Replace("aria-label=\"Показать номер телефона\"></button>", ">8 900 999-99-99</button>"));
        var detail = await Run(AvitoCandidatesPageScripts.BuildReadDetailPanelScript());
        Assert.Equal("", detail.GetProperty("phoneDigits").GetString());
    }

    [Fact]
    public async Task ConfirmedDetail_IsAppliedOnlyToOriginalCandidate()
    {
        await page.SetContentAsync(Card("Анна Иванова").Replace("aria-label=\"Показать номер телефона\"></button>", ">8 900 123-45-67</button>")
            + "<aside><div>Анна Иванова</div><button data-marker='job-application/call-button'>8 900 123-45-67</button><p>На вакансию <a href='https://www.avito.ru/9988776655'>Кладовщик</a></p></aside>");
        await Run(AvitoCandidatesPageScripts.BuildRememberCandidateTargetScript(0));
        var detail = await Run(AvitoCandidatesPageScripts.BuildReadDetailPanelScript());
        Assert.True(detail.GetProperty("hasPanel").GetBoolean());
        Assert.Equal("89001234567", detail.GetProperty("phoneDigits").GetString());
        Assert.Equal("https://www.avito.ru/9988776655", detail.GetProperty("vacancyUrl").GetString());
        await Run(AvitoCandidatesPageScripts.BuildApplyDetailEnrichmentScript("{\"0\":" + detail.GetRawText() + "}"));
        var original = (await Run(AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer())).GetProperty("candidates")[0];
        Assert.Equal("https://www.avito.ru/9988776655", original.GetProperty("vacancyUrl").GetString());
        await page.EvaluateExpressionAsync("document.querySelector('h3').textContent='Ольга Петрова'");
        var changed = (await Run(AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer())).GetProperty("candidates")[0];
        Assert.NotEqual("https://www.avito.ru/9988776655", changed.GetProperty("vacancyUrl").GetString());
        Assert.False((await Run(AvitoCandidatesPageScripts.BuildReadDetailPanelScript())).GetProperty("hasPanel").GetBoolean());
    }
}
