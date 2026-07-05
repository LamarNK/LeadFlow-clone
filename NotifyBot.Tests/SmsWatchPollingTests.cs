using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NotifyBot.Application.Abstractions;
using NotifyBot.Application.Models;
using NotifyBot.Application.Options;
using NotifyBot.Application.Services;
using NotifyBot.Domain.Entities;
using NotifyBot.Infrastructure.Parsing;

namespace NotifyBot.Tests;

public sealed class SmsWatchPollingTests
{
    private const string AeroflotSms =
        "Для оплаты в PJSC AEROFLOT 17,189.00 RUB Карта *3098; 3DS код: 894199";

    [Fact]
    public async Task FindWatchMatchesForChatsAsync_SingleApiCallMatchesMultipleChats()
    {
        var plusofon = new Mock<IPlusofonSmsClient>();
        plusofon
            .Setup(x => x.GetRecentIncomingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PlusofonSmsMessage(AeroflotSms, DateTimeOffset.UtcNow, true)
            ]);

        var cards = new Mock<ICardRepository>();
        cards
            .Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Card { Last4 = "3098", DestinationChatId = -100333, Enabled = true },
                new Card { Last4 = "1062", DestinationChatId = -100444, Enabled = true }
            ]);

        var service = CreateService(plusofon.Object, cards.Object);
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var matches = await service.FindWatchMatchesForChatsAsync([
            new SmsWatchSessionQuery(-100333, since, new HashSet<string>(StringComparer.Ordinal)),
            new SmsWatchSessionQuery(-100444, since, new HashSet<string>(StringComparer.Ordinal))
        ]);

        Assert.Single(matches);
        Assert.True(matches.ContainsKey(-100333));
        Assert.Equal("894199", matches[-100333][0].Info.Code);
        plusofon.Verify(x => x.GetRecentIncomingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FindWatchMatchesForChatsAsync_SkipsAlreadyDeliveredAndOldMessages()
    {
        var plusofon = new Mock<IPlusofonSmsClient>();
        plusofon
            .Setup(x => x.GetRecentIncomingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PlusofonSmsMessage(AeroflotSms, DateTimeOffset.UtcNow.AddMinutes(-10), true)
            ]);

        var cards = new Mock<ICardRepository>();
        cards
            .Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Card { Last4 = "3098", DestinationChatId = -100333, Enabled = true }
            ]);

        var service = CreateService(plusofon.Object, cards.Object);
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var excluded = new HashSet<string>(StringComparer.Ordinal) { AeroflotSms };
        var matches = await service.FindWatchMatchesForChatsAsync([
            new SmsWatchSessionQuery(-100333, since, excluded)
        ]);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task CheckAsync_NoSms_StartsWatch()
    {
        var plusofon = new Mock<IPlusofonSmsClient>();
        plusofon
            .Setup(x => x.GetRecentIncomingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var service = CreateService(plusofon.Object, new Mock<ICardRepository>().Object);
        var result = await service.CheckAsync(-100333);

        Assert.True(result.ShouldStartWatch);
        Assert.Contains("пока нет", result.Reply, StringComparison.OrdinalIgnoreCase);
    }

    private static SmsCheckService CreateService(IPlusofonSmsClient plusofon, ICardRepository cards) =>
        new(
            plusofon,
            new SmsParser(),
            cards,
            Options.Create(new PlusofonOptions
            {
                ApiToken = "test-token",
                SmsFetchLimit = 20
            }),
            NullLogger<SmsCheckService>.Instance);
}