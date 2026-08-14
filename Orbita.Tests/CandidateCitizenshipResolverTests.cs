using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CandidateCitizenshipResolverTests
{
    [Theory]
    [InlineData("Мужчина · 54 года · Гражданство: Россия · Опыт: Нет", "Россия")]
    [InlineData("Гражданство — Республика Беларусь Возраст — 42 года", "Республика Беларусь")]
    [InlineData("ГРАЖДАНСТВО Кыргызстан, возраст: 31", "Кыргызстан")]
    public void Resolve_ExtractsCitizenshipFromCandidateText(string text, string expected)
    {
        Assert.Equal(expected, CandidateCitizenshipResolver.Resolve(null, text));
    }

    [Fact]
    public void Resolve_ExplicitValueTakesPriorityAndIsNormalized()
    {
        Assert.Equal(
            "Россия",
            CandidateCitizenshipResolver.Resolve("  Россия  ", "Гражданство: Казахстан"));
    }

    [Fact]
    public void Resolve_TextWithoutLabel_ReturnsEmpty()
    {
        Assert.Empty(CandidateCitizenshipResolver.Resolve(null, "Москва, 35 лет, водитель"));
    }
}
