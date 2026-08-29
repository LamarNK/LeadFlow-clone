using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmLeadFileParserTests
{
    [Fact]
    public void Parse_SectionsAndCandidateRows_RecognizesNamesPhonesAndVacancies()
    {
        const string content = """
            РАЗНОРАБ

            Иванов Иван Иванович
            +7 999 111-22-33

            Петров Пётр
            8 (999) 222-33-44

            СВАРЩИК
            Сидоров Сидор Сидорович
            9993334455
            """;

        var result = CrmLeadFileParser.Parse(content);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal("Иванов Иван Иванович", result.Entries[0].FullName);
        Assert.Equal("РАЗНОРАБ", result.Entries[0].Vacancy);
        Assert.Equal("Петров Пётр", result.Entries[1].FullName);
        Assert.Equal("РАЗНОРАБ", result.Entries[1].Vacancy);
        Assert.Equal("Сидоров Сидор Сидорович", result.Entries[2].FullName);
        Assert.Equal("СВАРЩИК", result.Entries[2].Vacancy);
    }

    [Fact]
    public void Parse_RepeatedPhone_KeepsFirstAndReportsDuplicate()
    {
        const string content = """
            ВАКАНСИЯ 1
            Первый Кандидат
            +7 999 111-22-33
            ВАКАНСИЯ 2
            Второй Кандидат
            8 999 111 22 33
            """;

        var result = CrmLeadFileParser.Parse(content);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("Первый Кандидат", entry.FullName);
        Assert.Equal(1, result.DuplicateRowsInFile);
    }

    [Fact]
    public void Parse_TemporaryNumberAnnotation_KeepsCandidateNameAndSection()
    {
        const string content = """
            ОХРАНА

            недогреев Дмитрий Игоревич
            Временный номер
            +7 932 204-84-31

            Махмудов Тельман Зейнуллаевич
            +7 918 739-83-36
            """;

        var result = CrmLeadFileParser.Parse(content);

        Assert.Collection(
            result.Entries,
            first =>
            {
                Assert.Equal("недогреев Дмитрий Игоревич", first.FullName);
                Assert.Equal("+7 932 204-84-31", first.PhoneRaw);
                Assert.Equal("ОХРАНА", first.Vacancy);
            },
            second =>
            {
                Assert.Equal("Махмудов Тельман Зейнуллаевич", second.FullName);
                Assert.Equal("ОХРАНА", second.Vacancy);
            });
    }

    [Fact]
    public void Parse_DifferentAnnotationsAndNameCasing_DoNotBecomeNamesOrVacancies()
    {
        const string content = """
            ОХРАНА
            ПЕТРОВ ПЕТР ПЕТРОВИЧ
            Контактный телефон
            +7 999 111-22-33

            сидоров сидор сидорович
            служебная пометка
            8 999 222 33 44

            СЛЕСАРЯ
            Алексей
            Основной номер
            +7 999 333-44-55
            """;

        var result = CrmLeadFileParser.Parse(content);

        Assert.Collection(
            result.Entries,
            first =>
            {
                Assert.Equal("ПЕТРОВ ПЕТР ПЕТРОВИЧ", first.FullName);
                Assert.Equal("ОХРАНА", first.Vacancy);
            },
            second =>
            {
                Assert.Equal("сидоров сидор сидорович", second.FullName);
                Assert.Equal("ОХРАНА", second.Vacancy);
            },
            third =>
            {
                Assert.Equal("Алексей", third.FullName);
                Assert.Equal("СЛЕСАРЯ", third.Vacancy);
            });
    }

    [Fact]
    public void Parse_ProfessionBeforeImperfectName_UsesLineClosestToPhone()
    {
        const string content = """
            Разнорабочий
            Дудников Денис Андреевич
            8 914 470-60-55
            Охранник
            КК
            +7 989 493-42-12
            Охранник
            wertul
            +7 989 492-81-97
            Охранник
            Семедов. Мурад. Селимбекович
            8 912 096-07-08
            Охранник
            василий
            +7 958 611-98-35
            """;

        var result = CrmLeadFileParser.Parse(content);

        Assert.Equal(5, result.Entries.Count);
        Assert.Equal("КК", result.Entries[1].FullName);
        Assert.Equal("Охранник", result.Entries[1].Vacancy);
        Assert.Equal("wertul", result.Entries[2].FullName);
        Assert.Equal("Семедов Мурад Селимбекович", result.Entries[3].FullName);
        Assert.Equal("василий", result.Entries[4].FullName);
        Assert.DoesNotContain(result.Entries, entry => entry.FullName == "Охранник");
    }

    [Theory]
    [InlineData("+7 999 111-22-33", "79991112233")]
    [InlineData("8 (999) 111 22 33", "79991112233")]
    [InlineData("9991112233", "79991112233")]
    public void TryNormalizePhone_SupportedRussianFormats_Normalizes(string source, string expected)
    {
        var success = CrmLeadFileParser.TryNormalizePhone(source, out var normalized);

        Assert.True(success);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("Иван Иванов")]
    [InlineData("12345")]
    [InlineData("+1 202 555 0100")]
    public void TryNormalizePhone_NonRussianPhoneOrText_ReturnsFalse(string source)
    {
        Assert.False(CrmLeadFileParser.TryNormalizePhone(source, out _));
    }
}
