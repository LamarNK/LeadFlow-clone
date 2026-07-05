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
        var result = await service.CheckAsync(-100333);

        Assert.False(result.ShouldStartWatch);
        Assert.Contains("645755", result.Reply);
        Assert.Contains("*1062", result.Reply);
        Assert.Matches(@"\[\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}\]", result.Reply);
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
        var result = await service.CheckAsync(-100333);

        Assert.False(result.ShouldStartWatch);
        Assert.Contains("645755", result.Reply);
    }

    [Fact]
    public async Task CheckAsync_Old3dsSms_ReturnsTooOldMessage()
    {
        var plusofon = new Mock<IPlusofonSmsClient>();
        plusofon
            .Setup(x => x.GetRecentIncomingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PlusofonSmsMessage(SampleSms, DateTimeOffset.UtcNow.AddMinutes(-20), true)
            ]);

        var cards = new Mock<ICardRepository>();
        cards
            .Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Card { Last4 = "1062", DestinationChatId = -100333, Enabled = true }
            ]);

        var service = CreateService(plusofon.Object, cards.Object);
        var result = await service.CheckAsync(-100333);

        Assert.True(result.ShouldStartWatch);
        Assert.Contains("15 мин", result.Reply);
        Assert.DoesNotContain("3DS код:", result.Reply);
    }

    [Fact]
    public async Task CheckAsync_ThreeCardsBound_ReturnsOnlyLatestSms()
    {
        var older =
            "Для оплаты в old.shop 100.00 RUB Карта *9669; 3DS код: 111111";
        var plusofon = new Mock<IPlusofonSmsClient>();
        plusofon
            .Setup(x => x.GetRecentIncomingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PlusofonSmsMessage(older, DateTimeOffset.UtcNow.AddMinutes(-10), true),
                new PlusofonSmsMessage(SampleSms, DateTimeOffset.UtcNow, true)
            ]);

        var cards = new Mock<ICardRepository>();
        cards
            .Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Card { Last4 = "1062", DestinationChatId = -100333, Enabled = true },
                new Card { Last4 = "9669", DestinationChatId = -100333, Enabled = true },
                new Card { Last4 = "3098", DestinationChatId = -100333, Enabled = true }
            ]);

        var service = CreateService(plusofon.Object, cards.Object);
        var result = await service.CheckAsync(-100333);

        Assert.False(result.ShouldStartWatch);
        Assert.Contains("645755", result.Reply);
        Assert.Contains("*1062", result.Reply);
        Assert.DoesNotContain("111111", result.Reply);
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
        var result = await service.CheckAsync(-100333);

        Assert.True(result.ShouldStartWatch);
        Assert.Contains("*3098", result.Reply);
        Assert.Contains("Нет SMS", result.Reply);
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

        var result = await service.CheckAsync(-100333);

        Assert.False(result.ShouldStartWatch);
        Assert.Contains("не настроен", result.Reply, StringComparison.OrdinalIgnoreCase);
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