using System.Text.Json;
using LeadFlow.Core.Services.Avito;
using PuppeteerSharp;
using Xunit;

namespace LeadFlow.Tests;

/// <summary>Production extraction JS against the two live Job CRM layouts from saved Avito dumps.</summary>
public sealed class AvitoResponsesPageVariantBrowserTests : IAsyncLifetime
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

    private async Task<JsonElement> ExtractAsync(string html)
    {
        await page.SetContentAsync(html);
        await page.EvaluateExpressionAsync(AvitoCandidatesPageScripts.BuildResetCandidateCollectionStateScript().TrimEnd(';'));
        var raw = await page.EvaluateExpressionAsync<string>(AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer());
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private async Task<JsonElement> DetectAsync(string html)
    {
        await page.SetContentAsync(html);
        var raw = await page.EvaluateExpressionAsync<string>(
            AvitoCandidatesPageScripts.BuildIsJobCrmResponsesPageScript().TrimEnd(';'));
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static string DetailedPage(string callButtonText = "+7 900 111-22-33") => $$"""
        <div data-marker="filters/status-list-content"></div>
        <div data-marker="job-applications/response-appearance/long"></div>
        <div data-marker="job-application/item" role="button">
          <h4>Иван Иванов</h4>
          <p>Мужчина · 40 лет · Гражданство: Россия · Опыт: Есть</p>
          <p>Откликнулся сегодня в 08:15 на вакансию «Охранник вахта» · Тюмень</p>
          <button data-marker="job-application/call-button">{{callButtonText}}</button>
        </div>
        """;

    private static string CompactPage() => """
        <div data-marker="filters/status-list-content"></div>
        <div data-marker="filters/responses-list-content"></div>
        <div data-marker="job-application/item">
          <h3>Пётр Петров</h3>
          <p>46 лет · Гражданство: Россия</p>
          <a data-marker="job-application/link/to-resume" href="https://www.avito.ru/1234567890">Охранник · Пермь · вчера, 10:37</a>
          <button data-marker="job-application/response/status-select-button">Новый кандидат</button>
          <button data-marker="job-application/phone">8 912 000-11-22</button>
          <button data-marker="job-crm/response/cv-button">Резюме</button>
        </div>
        """;

    [Fact]
    public async Task DetectsDetailedCrmLayoutAndParsesCard()
    {
        var detection = await DetectAsync(DetailedPage());
        Assert.True(detection.GetProperty("isJobCrm").GetBoolean());
        Assert.Equal(AvitoResponsesPageVariant.JobCrmDetailed, detection.GetProperty("pageVariant").GetString());

        var root = await ExtractAsync(DetailedPage());
        Assert.Equal(AvitoResponsesPageVariant.JobCrmDetailed, root.GetProperty("pageVariant").GetString());
        Assert.Equal(1, root.GetProperty("domItemCount").GetInt32());
        Assert.Equal(0, root.GetProperty("missingPhoneCount").GetInt32());
        var candidate = root.GetProperty("candidates")[0];
        Assert.Equal("Иван Иванов", candidate.GetProperty("fullName").GetString());
        Assert.Equal("+7 900 111-22-33", candidate.GetProperty("phone").GetString());
        Assert.Equal("40 лет", candidate.GetProperty("age").GetString());
        Assert.Equal("male", candidate.GetProperty("gender").GetString());
        Assert.Equal("Охранник вахта", candidate.GetProperty("vacancy").GetString());
        Assert.Equal("Тюмень", candidate.GetProperty("city").GetString());
        Assert.False(string.IsNullOrWhiteSpace(candidate.GetProperty("responseAt").GetString()));
        Assert.True(string.IsNullOrWhiteSpace(candidate.GetProperty("vacancyUrl").GetString()));
    }

    [Fact]
    public async Task DetailedLayoutWithoutPhone_CountsMissingAndExtractsNothing()
    {
        var root = await ExtractAsync(DetailedPage("Показать номер"));
        Assert.Equal(AvitoResponsesPageVariant.JobCrmDetailed, root.GetProperty("pageVariant").GetString());
        Assert.Equal(0, root.GetProperty("candidates").GetArrayLength());
        Assert.Equal(1, root.GetProperty("missingPhoneCount").GetInt32());
    }

    [Fact]
    public async Task DetectsCompactCrmLayoutAndParsesCard()
    {
        var detection = await DetectAsync(CompactPage());
        Assert.True(detection.GetProperty("isJobCrm").GetBoolean());
        Assert.Equal(AvitoResponsesPageVariant.JobCrmCompact, detection.GetProperty("pageVariant").GetString());

        var root = await ExtractAsync(CompactPage());
        Assert.Equal(AvitoResponsesPageVariant.JobCrmCompact, root.GetProperty("pageVariant").GetString());
        Assert.Equal(1, root.GetProperty("domItemCount").GetInt32());
        Assert.Equal(1, root.GetProperty("domStatusCount").GetInt32());
        var candidate = root.GetProperty("candidates")[0];
        Assert.Equal("Пётр Петров", candidate.GetProperty("fullName").GetString());
        Assert.Equal("8 912 000-11-22", candidate.GetProperty("phone").GetString());
        Assert.Equal("46 лет", candidate.GetProperty("age").GetString());
        Assert.Equal("Охранник", candidate.GetProperty("vacancy").GetString());
        Assert.Equal("Пермь", candidate.GetProperty("city").GetString());
        Assert.Equal("https://www.avito.ru/1234567890", candidate.GetProperty("vacancyUrl").GetString());
        Assert.False(string.IsNullOrWhiteSpace(candidate.GetProperty("responseAt").GetString()));
    }

    [Fact]
    public async Task CompactShortMonthDate_ParsesWithoutTime()
    {
        var html = CompactPage().Replace("вчера, 10:37", "17 сент.", StringComparison.Ordinal);
        var root = await ExtractAsync(html);
        var responseAt = root.GetProperty("candidates")[0].GetProperty("responseAt").GetString();
        Assert.False(string.IsNullOrWhiteSpace(responseAt));
        Assert.Contains("2026-09-1", responseAt, StringComparison.Ordinal);
    }
}
