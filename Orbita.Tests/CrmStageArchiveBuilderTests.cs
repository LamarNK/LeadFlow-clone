using System.IO.Compression;
using System.Text;
using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class CrmStageArchiveBuilderTests
{
    [Fact]
    public void Build_CreatesOneExcelCompatibleTableAndKeepsAllPhones()
    {
        var cardId = Guid.Parse("91000000-0000-0000-0000-000000000001");
        var card = CreateCard(
            cardId,
            "=ОПАСНАЯ ФОРМУЛА",
            "+7 900 111-22-33",
            [
                "+7 900 111-22-33",
                "+7 900 222-33-44",
                "+7 900 333-44-55",
                "+7 900 444-55-66",
                "+7 900 555-66-77",
                "+7 900 666-77-88"
            ]);

        var result = CrmStageArchiveBuilder.Build(
            "Офис 1",
            "Робот",
            [card],
            new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc));
        using var archive = new ZipArchive(result.Stream, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);

        Assert.Equal(1, result.CardCount);
        Assert.Equal("Сделки.csv", Assert.Single(archive.Entries).FullName);

        var cards = ReadEntry(archive, "Сделки.csv");

        Assert.Contains("\"'=ОПАСНАЯ ФОРМУЛА\"", cards);
        Assert.Contains("+7 900 111-22-33", cards);
        Assert.Contains("+7 900 444-55-66", cards);
        Assert.Contains("+7 900 666-77-88", cards);
        Assert.Contains("Телефон 6", cards);
        Assert.DoesNotContain(cardId.ToString(), cards);
        Assert.DoesNotContain("ID карточки", cards);
        Assert.DoesNotContain("Создана UTC", cards);
        Assert.DoesNotContain("Ссылка источника", cards);
        Assert.DoesNotContain("Ссылка вакансии", cards);
        Assert.DoesNotContain("Мессенджер", cards);
        Assert.DoesNotContain("Аккаунт/источник", cards);
        Assert.DoesNotContain("https://example.test/source/1", cards);
        Assert.Equal(2, cards.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = Assert.Single(archive.Entries, item => item.FullName == name);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static CrmCandidateCardDto CreateCard(
        Guid id,
        string fullName,
        string phone,
        IReadOnlyList<string> phones) =>
        new(
            Id: id,
            ResponseId: Guid.Parse("92000000-0000-0000-0000-000000000001"),
            FullName: fullName,
            Age: 31,
            PhoneRaw: phone,
            City: "Екатеринбург",
            Vacancy: "Охранник",
            MessengerUrl: null,
            Stage: "Робот",
            ManagerUserId: "manager-1",
            ManagerName: "Менеджер Один",
            IsInActiveLoad: true,
            CreatedAtUtc: new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
            StageChangedAtUtc: new DateTime(2026, 9, 1, 8, 5, 0, DateTimeKind.Utc),
            LastContactAtUtc: null,
            NextActionAtUtc: null,
            IsClosed: false,
            CloseReason: null,
            OpenTaskCount: 0,
            HasOverdueTask: false,
            HoursInStage: 0.5,
            SourceUrl: "https://example.test/source/1",
            VacancyUrl: null,
            AccountName: "Источник",
            SourceResponseId: "external-1",
            ContactPhones: phones);
}
