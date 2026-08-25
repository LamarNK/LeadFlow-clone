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
