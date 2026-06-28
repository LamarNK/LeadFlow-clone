using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class CandidateParserTests
{
    private readonly CandidateParser _sut = new();

    [Fact]
    public void ParseName_FullTriple_SplitsIntoLastFirstMiddle()
    {
        var name = _sut.ParseName("Иванов Иван Иванович");
        Assert.Equal("Иванов", name.LastName);
        Assert.Equal("Иван", name.FirstName);
        Assert.Equal("Иванович", name.MiddleName);
    }

    [Fact]
    public void ParseName_OnlyLastName_OtherFieldsEmpty()
    {
        var name = _sut.ParseName("Иванов");
        Assert.Equal("Иванов", name.LastName);
        Assert.Equal(string.Empty, name.FirstName);
        Assert.Equal(string.Empty, name.MiddleName);
    }

    [Fact]
    public void ParseName_TrimsAndSkipsExtraWhitespace()
    {
        var name = _sut.ParseName("   Иванов    Иван   ");
        Assert.Equal("Иванов", name.LastName);
        Assert.Equal("Иван", name.FirstName);
        Assert.Equal(string.Empty, name.MiddleName);
    }

    [Fact]
    public void ParseName_Empty_ReturnsEmptyFields()
    {
        var name = _sut.ParseName(string.Empty);
        Assert.Equal(string.Empty, name.LastName);
        Assert.Equal(string.Empty, name.FirstName);
        Assert.Equal(string.Empty, name.MiddleName);
    }

    [Fact]
    public void BuildPreview_FillsCoreFieldsFromResponse()
    {
        var response = BuildSampleResponse();
        var settings = new BitrixSettings { LeadSource = "Авито" };

        var preview = _sut.BuildPreview(response, settings);

        Assert.Equal("Иванов Иван Иванович", preview.Title);
        Assert.Equal("Иван", preview.Name);
        Assert.Equal("Иванов", preview.LastName);
        Assert.Equal("Иванович", preview.SecondName);
        Assert.Equal("+7 900 000-00-00", preview.Phone);
        Assert.Equal("Москва", preview.City);
        Assert.Equal("Продавец", preview.Vacancy);
        Assert.Equal("Авито", preview.Source);
    }

    [Fact]
    public void BuildPreview_CommentsContainAllSummaryFields()
    {
        var response = BuildSampleResponse();
        var settings = new BitrixSettings();

        var preview = _sut.BuildPreview(response, settings);

        Assert.Contains("ФИО: Иванов Иван Иванович", preview.Comments);
        Assert.Contains("Телефон: +7 900 000-00-00", preview.Comments);
        Assert.Contains("Возраст: 30", preview.Comments);
        Assert.Contains("Вакансия: Продавец", preview.Comments);
        Assert.Contains("Город: Москва", preview.Comments);
        Assert.Contains("Источник: Авито", preview.Comments);
        Assert.Contains("Ссылка на вакансию: https://www.avito.ru/item/1", preview.Comments);
        Assert.Contains("ID отклика (источник): avito-123", preview.Comments);
        Assert.Contains("Ссылка на мессенджер: https://www.avito.ru/msg/1", preview.Comments);
        Assert.Contains("Текст отклика (фрагмент): Привет", preview.Comments);
        Assert.Contains("Аккаунт Авито: TestAcc", preview.Comments);
        Assert.Contains("Дата отклика:", preview.Comments);
    }

    [Fact]
    public void BuildPreview_NullAge_FormattedAsDash()
    {
        var response = BuildSampleResponse();
        response.Age = null;

        var preview = _sut.BuildPreview(response, new BitrixSettings());

        Assert.Contains("Возраст: -", preview.Comments);
    }

    private static CandidateResponse BuildSampleResponse() => new()
    {
        AccountName = "TestAcc",
        SourceResponseId = "avito-123",
        MessengerUrl = "https://www.avito.ru/msg/1",
        RawText = "Привет, готов работать",
        FullName = "Иванов Иван Иванович",
        FirstName = "Иван",
        LastName = "Иванов",
        MiddleName = "Иванович",
        PhoneRaw = "+7 900 000-00-00",
        City = "Москва",
        Vacancy = "Продавец",
        Age = 30,
        VacancyUrl = "https://www.avito.ru/item/1",
        CreatedAt = new DateTime(2026, 5, 6, 10, 30, 0, DateTimeKind.Utc)
    };
}
