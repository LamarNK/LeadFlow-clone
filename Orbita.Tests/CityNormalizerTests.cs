using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CityNormalizerTests
{
    [Fact]
    public void Normalize_ExpandsAbbreviationAndIgnoresCase()
    {
        var left = CityNormalizer.Normalize("РП Чик");
        var right = CityNormalizer.Normalize("рабочий поселок чик");

        Assert.Equal(right, left);
        Assert.True(CityNormalizer.IsMatch("РП Чик", "рабочий поселок Чик"));
    }

    [Fact]
    public void Normalize_CollapsesWhitespace()
    {
        Assert.True(CityNormalizer.IsMatch("  Москва  ", "москва"));
    }
}