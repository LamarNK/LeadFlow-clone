using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoCandidatesExtractionSummaryTests
{
    [Theory]
    [InlineData("https://www.avito.ru/profile/candidates", "legacy", "классическая (/profile/candidates)")]
    [InlineData("https://www.avito.ru/profile/candidates", "job-crm-detailed", "CRM подробный вид")]
    [InlineData("https://www.avito.ru/profile/candidates", "job-crm-compact", "CRM компактный список")]
    [InlineData("https://www.avito.ru/profile/job/responses", "job-crm", "CRM")]
    [InlineData("https://www.avito.ru/profile/dashboard", "unknown", "неизвестный URL (https://www.avito.ru/profile/dashboard)")]
    public void DescribePageVariant_ReturnsReadableLabel(string url, string variant, string expected)
    {
        Assert.Equal(expected, AvitoCandidatesExtractionSummary.DescribePageVariant(url, variant));
    }

    [Fact]
    public void FormatLogLine_IncludesCountsAndSamples()
    {
        var summary = new AvitoCandidatesExtractionSummary(
            "https://www.avito.ru/profile/job/responses",
            "job-crm",
            12,
            10,
            8,
            7,
            1,
            2,
            ["Иван Иванов (Водитель)", "Мария Петрова (Курьер)"]);

        var text = summary.FormatLogLine();

        Assert.Contains("CRM", text);
        Assert.Contains("в DOM 12 карточек", text);
        Assert.Contains("скрипт извлёк 8, валидных 7", text);
        Assert.Contains("отфильтровано 1", text);
        Assert.Contains("новых к публикации 2", text);
        Assert.Contains("Иван Иванов", text);
    }
}