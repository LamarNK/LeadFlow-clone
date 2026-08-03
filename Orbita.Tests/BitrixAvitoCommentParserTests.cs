using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixAvitoCommentParserTests
{
    [Fact]
    public void Parse_ExtractsLeadFlowCommentFields()
    {
        var result = BitrixAvitoCommentParser.Parse(
            """
            ФИО: Иванов Иван
            Возраст: 34
            Вакансия: Сварщик вахта с питанием
            Город: посёлок Сибирский
            Источник: Авито
            """);

        Assert.Equal(34, result.Age);
        Assert.Equal("Сварщик вахта с питанием", result.Profession);
        Assert.Equal("посёлок Сибирский", result.City);
    }

    [Theory]
    [InlineData("Возраст: -")]
    [InlineData("Возраст: 0")]
    [InlineData("Возраст: 121")]
    public void Parse_RejectsInvalidAge(string comments)
    {
        Assert.Null(BitrixAvitoCommentParser.Parse(comments).Age);
    }
}
