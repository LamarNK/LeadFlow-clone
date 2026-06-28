using System.Text.Json;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoCandidatesJsonParserTests
{
    [Fact]
    public void ParseCandidates_ExtractsFields()
    {
        var account = new AvitoAccount { Id = Guid.Parse("a1111111-1111-1111-1111-111111111111"), DisplayName = "TestAcc" };
        using var doc = JsonDocument.Parse(
            """
            {
              "candidates": [
                {
                  "fullName": "Иван Петров",
                  "phone": "+7 900 000-00-00",
                  "sourceResponseId": "id-1",
                  "vacancy": "Продавец",
                  "city": "Москва",
                  "vacancyUrl": "/item/1",
                  "messengerUrl": "https://www.avito.ru/messenger",
                  "rawText": "line",
                  "age": "25 лет"
                }
              ]
            }
            """);

        var list = AvitoCandidatesJsonParser.ParseCandidates(doc.RootElement, account);
        Assert.Single(list);
        var c = list[0];
        Assert.Equal("Иван Петров", c.FullName);
        Assert.Equal("+7 900 000-00-00", c.PhoneRaw);
        Assert.Equal("id-1", c.SourceResponseId);
        Assert.Equal("Продавец", c.Vacancy);
        Assert.Equal("Москва", c.City);
        Assert.Equal(25, c.Age);
        Assert.Equal(account.Id, c.AccountId);
        Assert.Equal("TestAcc", c.AccountName);
    }

    [Theory]
    [InlineData("30 лет", 30)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void ParseAge_ParsesLeadingDigits(string? input, int? expected)
    {
        Assert.Equal(expected, AvitoCandidatesJsonParser.ParseAge(input));
    }

    [Fact]
    public void ParseCandidates_SkipsIncompleteRows()
    {
        var account = new AvitoAccount { Id = Guid.NewGuid(), DisplayName = "A" };
        using var doc = JsonDocument.Parse(
            """
            {"candidates":[{"fullName":"","phone":"1","sourceResponseId":"x"}]}
            """);

        Assert.Empty(AvitoCandidatesJsonParser.ParseCandidates(doc.RootElement, account));
    }

    [Fact]
    public void ParseCandidates_NoCandidatesProperty_ReturnsEmpty()
    {
        var account = new AvitoAccount { Id = Guid.NewGuid(), DisplayName = "A" };
        using var doc = JsonDocument.Parse("{}");

        Assert.Empty(AvitoCandidatesJsonParser.ParseCandidates(doc.RootElement, account));
    }

    [Fact]
    public void ParseCandidates_EmptyCandidatesArray_ReturnsEmpty()
    {
        var account = new AvitoAccount { Id = Guid.NewGuid(), DisplayName = "A" };
        using var doc = JsonDocument.Parse("""{"candidates":[]}""");

        Assert.Empty(AvitoCandidatesJsonParser.ParseCandidates(doc.RootElement, account));
    }

    [Fact]
    public void ParseCandidates_LegacySourceUrlMatchesCandidatesPage_VacancyUrlStaysEmpty()
    {
        var account = new AvitoAccount { Id = Guid.NewGuid(), DisplayName = "A" };
        var json =
            $$"""
            {
              "candidates": [
                {
                  "fullName": "А А",
                  "phone": "+7 900 000-00-00",
                  "sourceResponseId": "id-1",
                  "sourceUrl": "{{AvitoResponseSource.CandidatesPageUrl}}"
                }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);

        var list = AvitoCandidatesJsonParser.ParseCandidates(doc.RootElement, account);
        Assert.Single(list);
        Assert.Equal(string.Empty, list[0].VacancyUrl);
    }

    [Fact]
    public void ParseCandidates_LegacySourceUrlDifferent_FillsVacancyUrl()
    {
        var account = new AvitoAccount { Id = Guid.NewGuid(), DisplayName = "A" };
        using var doc = JsonDocument.Parse(
            """
            {
              "candidates": [
                {
                  "fullName": "А А",
                  "phone": "+7 900 000-00-00",
                  "sourceResponseId": "id-1",
                  "sourceUrl": "https://www.avito.ru/item/42"
                }
              ]
            }
            """);

        var list = AvitoCandidatesJsonParser.ParseCandidates(doc.RootElement, account);
        Assert.Single(list);
        Assert.Equal("https://www.avito.ru/item/42", list[0].VacancyUrl);
    }

    [Fact]
    public void ParseCandidates_VacancyUrlPresent_LegacySourceUrlIgnored()
    {
        var account = new AvitoAccount { Id = Guid.NewGuid(), DisplayName = "A" };
        using var doc = JsonDocument.Parse(
            """
            {
              "candidates": [
                {
                  "fullName": "А А",
                  "phone": "+7 900 000-00-00",
                  "sourceResponseId": "id-1",
                  "vacancyUrl": "https://www.avito.ru/item/1",
                  "sourceUrl": "https://www.avito.ru/item/2"
                }
              ]
            }
            """);

        var list = AvitoCandidatesJsonParser.ParseCandidates(doc.RootElement, account);
        Assert.Equal("https://www.avito.ru/item/1", list[0].VacancyUrl);
    }

    [Fact]
    public void ParseCandidates_ExtractsMessengerUrlAndRawText()
    {
        var account = new AvitoAccount { Id = Guid.NewGuid(), DisplayName = "A" };
        using var doc = JsonDocument.Parse(
            """
            {
              "candidates": [
                {
                  "fullName": "А А",
                  "phone": "+7 900 000-00-00",
                  "sourceResponseId": "id-1",
                  "messengerUrl": "https://www.avito.ru/messenger/abc",
                  "rawText": "Текст карточки\nвторая строка"
                }
              ]
            }
            """);

        var list = AvitoCandidatesJsonParser.ParseCandidates(doc.RootElement, account);
        Assert.Equal("https://www.avito.ru/messenger/abc", list[0].MessengerUrl);
        Assert.Contains("Текст карточки", list[0].RawText);
    }

    [Fact]
    public void ParseCandidates_MultipleItems_PreservesOrder()
    {
        var account = new AvitoAccount { Id = Guid.NewGuid(), DisplayName = "Acc" };
        using var doc = JsonDocument.Parse(
            """
            {
              "candidates": [
                {"fullName":"A","phone":"1","sourceResponseId":"id-1"},
                {"fullName":"B","phone":"2","sourceResponseId":"id-2"},
                {"fullName":"C","phone":"3","sourceResponseId":"id-3"}
              ]
            }
            """);

        var list = AvitoCandidatesJsonParser.ParseCandidates(doc.RootElement, account);
        Assert.Equal(new[] { "id-1", "id-2", "id-3" }, list.Select(x => x.SourceResponseId).ToArray());
        Assert.Equal(new[] { "A", "B", "C" }, list.Select(x => x.FullName).ToArray());
    }
}
