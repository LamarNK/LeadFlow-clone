using System.Text.Json;
using NotifyBot.Api.Dto;

namespace NotifyBot.Tests;

public sealed class PlusofonWebhookDtoTests
{
    [Theory]
    [InlineData("""{"text":"hello"}""", "hello")]
    [InlineData("""{"message":"hello"}""", "hello")]
    [InlineData("""{"body":"hello"}""", "hello")]
    [InlineData("""{"sms_text":"hello"}""", "hello")]
    [InlineData("""{"content":"hello"}""", "hello")]
    [InlineData("""{"data":{"text":"hello"}}""", "hello")]
    public void Deserialize_ExtractsMessageText(string json, string expected)
    {
        var dto = JsonSerializer.Deserialize<PlusofonWebhookDto>(json);

        Assert.NotNull(dto);
        Assert.Equal(expected, dto.GetMessageText());
    }
}