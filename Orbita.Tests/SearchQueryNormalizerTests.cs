using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class SearchQueryNormalizerTests
{
    [Theory]
    [InlineData("  Иван   Петров  ", "Иван Петров")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Normalize_TrimsAndCollapsesWhitespace(string? raw, string? expected)
    {
        Assert.Equal(expected, SearchQueryNormalizer.Normalize(raw));
    }

    [Fact]
    public void Tokenize_SplitsOnWhitespace()
    {
        Assert.Equal(["Иван", "Москва"], SearchQueryNormalizer.Tokenize("  Иван   Москва "));
    }

    [Fact]
    public void MatchesTokens_IsCaseInsensitive()
    {
        Assert.True(SearchQueryNormalizer.MatchesTokens("МОСКВА", "г. Москва"));
        Assert.True(SearchQueryNormalizer.MatchesTokens("ivan", "Ivan Petrov"));
    }

    [Fact]
    public void MatchesTokens_RequiresAllTokens()
    {
        Assert.True(SearchQueryNormalizer.MatchesTokens("иван москва", "Иван Петров", "Москва"));
        Assert.False(SearchQueryNormalizer.MatchesTokens("иван казань", "Иван Петров", "Москва"));
    }

    [Fact]
    public void MatchesTokens_FindsPhoneByDigits()
    {
        Assert.True(SearchQueryNormalizer.MatchesTokens("999 123", "+7 999 123-45-67", "79991234567"));
        Assert.True(SearchQueryNormalizer.MatchesPhone("+7 (999) 123-45-67", "79991234567", "999123"));
    }

    [Fact]
    public void EscapeILikeLiteral_EscapesWildcards()
    {
        Assert.Equal("100\\% done", SearchQueryNormalizer.EscapeILikeLiteral("100% done"));
        Assert.Equal("a\\_b", SearchQueryNormalizer.EscapeILikeLiteral("a_b"));
    }
}