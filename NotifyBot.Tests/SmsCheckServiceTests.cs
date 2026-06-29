using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NotifyBot.Application.Abstractions;
using NotifyBot.Application.Options;
using NotifyBot.Application.Services;
using NotifyBot.Domain.Entities;
using NotifyBot.Infrastructure.Parsing;

namespace NotifyBot.Tests;

public sealed class SmsCheckServiceTests
{
    private const string SampleSms =
        "Для оплаты в ticket.rzd.ru 6,194.60 RUB Карта *1062; 3DS код: 645755";

    [Fact]
    public async Task CheckAsync_WithBoundCard_ReturnsLatestMatchingCode()
    {
        var plusofon = new Mock<IPlusofonSmsClient>();
        plusofon
            .Setup(x => x.GetRecentIncomingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PlusofonSmsMessage("старый текст", DateTimeOffset.UtcNow.AddMinutes(-5), true),
                new PlusofonSmsMessage(SampleSms, DateTimeOffset.UtcNow, true)
            ]);

        var cards = new Mock<ICardRepository>();
        cards
            .Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Card { Last4 = "1062", DestinationChatId = -100333, Enabled = true }
            ]);

        var service = CreateService(plusofon.Object, cards.Object);
        var reply = await service.CheckAsync(-100333);

        Assert.Contains("645755", reply);
        Assert.Contains("*1062", reply);
    }

    [Fact]
    public async Task CheckAsync_NoBoundCards_ReturnsLatestCode()
    {
        var plusofon = new Mock<IPlusofonSmsClient>();
        plusofon
            .Setup(x => x.GetRecentIncomingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PlusofonSmsMessage(SampleSms, DateTimeOffset.UtcNow, true)]);

        var cards = new Mock<ICardRepository>();
        cards
            .Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var service = CreateService(plusofon.Object, cards.Object);
        var reply = await service.CheckAsync(-100333);

        Assert.Contains("645755", reply);
    }

    [Fact]
    public async Task CheckAsync_BoundCardButNoMatch_ReturnsHint()
    {
        var plusofon = new Mock<IPlusofonSmsClient>();
        plusofon
            .Setup(x => x.GetRecentIncomingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PlusofonSmsMessage(SampleSms, DateTimeOffset.UtcNow, true)]);

        var cards = new Mock<ICardRepository>();
        cards
            .Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Card { Last4 = "3098", DestinationChatId = -100333, Enabled = true }
            ]);

        var service = CreateService(plusofon.Object, cards.Object);
        var reply = await service.CheckAsync(-100333);

        Assert.Contains("*3098", reply);
        Assert.Contains("Нет SMS", reply);
    }

    [Fact]
    public async Task ListAllForDebugAsync_ReturnsFormattedMessages()
    {
        var plusofon = new Mock<IPlusofonSmsClient>();
        plusofon
            .Setup(x => x.GetRecentAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PlusofonSmsMessage("Тест 1", DateTimeOffset.UtcNow, true, "VTB", "79019601328"),
                new PlusofonSmsMessage("Тест 2", DateTimeOffset.UtcNow.AddMinutes(-1), false, "79019601328", "900")
            ]);

        var service = CreateService(plusofon.Object, new Mock<ICardRepository>().Object);
        var reply = await service.ListAllForDebugAsync();

        Assert.Contains("Последние SMS (2)", reply);
        Assert.Contains("Тест 1", reply);
        Assert.Contains("VTB", reply);
        Assert.Contains("исх", reply);
    }

    [Fact]
    public async Task CheckAsync_NoApiCredentials_ReturnsConfigMessage()
    {
        var service = CreateService(
            new Mock<IPlusofonSmsClient>().Object,
            new Mock<ICardRepository>().Object,
            apiToken: "");

        var reply = await service.CheckAsync(-100333);

        Assert.Contains("не настроен", reply, StringComparison.OrdinalIgnoreCase);
    }

    private static SmsCheckService CreateService(
        IPlusofonSmsClient plusofon,
        ICardRepository cards,
        string apiToken = "test-token") =>
        new(
            plusofon,
            new SmsParser(),
            cards,
            Options.Create(new PlusofonOptions
            {
                ApiToken = apiToken,
                SmsFetchLimit = 20
            }),
            NullLogger<SmsCheckService>.Instance);
}