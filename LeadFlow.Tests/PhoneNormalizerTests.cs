using LeadFlow.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class PhoneNormalizerTests
{
    private readonly PhoneNormalizer _sut = new();

    [Theory]
    [InlineData("89001234567", "79001234567")]
    [InlineData("8 (900) 123-45-67", "79001234567")]
    public void Normalize_ElevenDigitsStartingWith8_ReplacedBy7(string input, string expected)
    {
        Assert.Equal(expected, _sut.Normalize(input));
    }

    [Theory]
    [InlineData("9001234567", "79001234567")]
    [InlineData("(900) 123-45-67", "79001234567")]
    public void Normalize_TenDigits_PrefixedWith7(string input, string expected)
    {
        Assert.Equal(expected, _sut.Normalize(input));
    }

    [Theory]
    [InlineData("79001234567", "79001234567")]
    [InlineData("+7 900 123-45-67", "79001234567")]
    public void Normalize_ElevenDigitsStartingWith7_KeptAsIs(string input, string expected)
    {
        Assert.Equal(expected, _sut.Normalize(input));
    }

    [Fact]
    public void Normalize_GarbageSymbols_OnlyDigitsKept()
    {
        Assert.Equal("79000000000", _sut.Normalize("+7 (900) 000-00-00"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void Normalize_EmptyOrNoDigits_ReturnsEmpty(string input)
    {
        Assert.Equal(string.Empty, _sut.Normalize(input));
    }

    [Theory]
    [InlineData("123", "123")]
    [InlineData("9001", "9001")]
    [InlineData("12345678901234", "12345678901234")]
    public void Normalize_OutOfStandardLengths_ReturnsDigitsOnlyWithoutPrefix(string input, string expected)
    {
        Assert.Equal(expected, _sut.Normalize(input));
    }
}
