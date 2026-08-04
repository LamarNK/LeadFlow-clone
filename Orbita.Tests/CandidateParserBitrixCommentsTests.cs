using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Contracts;
using Xunit;

namespace Orbita.Tests;

public sealed class CandidateParserBitrixCommentsTests
{
    private readonly CandidateParser _sut = new();

    [Fact]
    public void BuildPreview_OmitsMessengerLink()
    {
        var lead = BuildLead();
        lead.MessengerUrl = "https://www.avito.ru/msg/1";

        var preview = _sut.BuildPreview(lead, new OrbitaBitrixSettings());

        Assert.DoesNotContain("мессенджер", preview.Comments, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ссылка на вакансию:", preview.Comments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPreview_IncludesPhoneHistory_WhenMultipleNumbers()
    {
        var lead = BuildLead();
        lead.PhoneRaw = "+7 951 731-88-44";
        lead.PhoneNormalized = "79517318844";
        lead.PhoneHistory =
        [
            new CandidateLeadPhoneHistoryItem("+7 939 409-08-33", "79394090833", new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc)),
            new CandidateLeadPhoneHistoryItem("+7 951 731-88-44", "79517318844", new DateTime(2026, 8, 4, 14, 0, 0, DateTimeKind.Utc))
        ];

        var preview = _sut.BuildPreview(lead, new OrbitaBitrixSettings());

        Assert.Contains("Телефон: +7 951 731-88-44", preview.Comments, StringComparison.Ordinal);
        Assert.Contains("История телефонов:", preview.Comments, StringComparison.Ordinal);
        Assert.Contains("+7 939 409-08-33", preview.Comments, StringComparison.Ordinal);
        Assert.Contains("+7 951 731-88-44", preview.Comments, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPreview_SkipsHistoryBlock_WhenOnlyCurrentPhone()
    {
        var lead = BuildLead();
        lead.PhoneHistory =
        [
            new CandidateLeadPhoneHistoryItem(lead.PhoneRaw, lead.PhoneNormalized, DateTime.UtcNow)
        ];

        var preview = _sut.BuildPreview(lead, new OrbitaBitrixSettings());

        Assert.DoesNotContain("История телефонов:", preview.Comments, StringComparison.Ordinal);
        Assert.Contains("Телефон: +7 900 000-00-00", preview.Comments, StringComparison.Ordinal);
    }

    private static CandidateLead BuildLead() => new()
    {
        AccountName = "TestAcc",
        SourceResponseId = "avito-123",
        FullName = "Иванов Иван Иванович",
        FirstName = "Иван",
        LastName = "Иванов",
        MiddleName = "Иванович",
        PhoneRaw = "+7 900 000-00-00",
        PhoneNormalized = "79000000000",
        City = "Москва",
        Vacancy = "Продавец",
        Age = 30,
        VacancyUrl = "https://www.avito.ru/item/1",
        CreatedAt = new DateTime(2026, 5, 6, 10, 30, 0, DateTimeKind.Utc),
        RawText = "Привет"
    };
}
