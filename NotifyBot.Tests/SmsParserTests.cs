using NotifyBot.Infrastructure.Parsing;

namespace NotifyBot.Tests;

public sealed class SmsParserTests
{
    private readonly SmsParser _parser = new();

    [Theory]
    [InlineData("Для оплаты в ticket.rzd.ru 6,194.60 RUB Карта *1062; 3DS код: 645755", "1062", "645755", "6,194.60", "ticket.rzd.ru")]
    [InlineData("Для оплаты в ticket.rzd.ru 5,786.20 RUB Карта *9669; 3DS код: 374860", "9669", "374860", "5,786.20", "ticket.rzd.ru")]
    [InlineData("Для оплаты в ticket.rzd.ru 6,064.80 RUB Карта *3098; 3DS код: 442870", "3098", "442870", "6,064.80", "ticket.rzd.ru")]
    [InlineData("Для оплаты в PJSC AEROFLOT 17,189.00 RUB Карта *3098; 3DS код: 894199", "3098", "894199", "17,189.00", "PJSC AEROFLOT")]
    [InlineData("Для оплаты в SOME SHOP NAME 1,234.56 RUB Карта *1062;3DS код:123456", "1062", "123456", "1,234.56", "SOME SHOP NAME")]
    public void Parse_ValidSms_ReturnsSmsInfo(
        string rawText,
        string expectedCard,
        string expectedCode,
        string expectedAmount,
        string expectedMerchant)
    {
        var result = _parser.Parse(rawText);

        Assert.NotNull(result);
        Assert.Equal(expectedCard, result.CardLast4);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(expectedAmount, result.Amount);
        Assert.Equal(expectedMerchant, result.Merchant);
        Assert.Equal(rawText, result.RawText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Случайное SMS без 3DS")]
    public void Parse_InvalidSms_ReturnsNull(string rawText)
    {
        var result = _parser.Parse(rawText);

        Assert.Null(result);
    }
}