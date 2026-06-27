using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NotifyBot.Application.Abstractions;
using NotifyBot.Application.Services;
using NotifyBot.Domain.Entities;
using NotifyBot.Domain.Models;
using NotifyBot.Infrastructure.Parsing;

namespace NotifyBot.Tests;

public sealed class SmsRoutingServiceTests
{
    private const string SampleSms =
        "Для оплаты в ticket.rzd.ru 6,194.60 RUB Карта *1062; 3DS код: 645755";

    [Fact]
    public async Task ProcessWebhookAsync_KnownCard_DispatchesToDestination()
    {
        var cardRepository = new Mock<ICardRepository>();
        cardRepository
            .Setup(x => x.GetByLast4Async("1062", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Card { Id = 1, Last4 = "1062", DestinationChatId = -100333, Enabled = true });

        var dispatcher = new Mock<INotificationDispatcher>();
        var service = CreateService(cardRepository.Object, dispatcher.Object);

        await service.ProcessWebhookAsync(SampleSms);

        dispatcher.Verify(
            x => x.DispatchToDestinationAsync(
                -100333,
                It.Is<string>(m => m.Contains("645755") && m.Contains("*1062")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        dispatcher.Verify(
            x => x.DispatchToAdminsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessWebhookAsync_UnboundCard_DispatchesToAdmins()
    {
        var cardRepository = new Mock<ICardRepository>();
        cardRepository
            .Setup(x => x.GetByLast4Async("1062", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Card { Id = 1, Last4 = "1062", Enabled = true, DestinationChatId = null });

        var dispatcher = new Mock<INotificationDispatcher>();
        var service = CreateService(cardRepository.Object, dispatcher.Object);

        await service.ProcessWebhookAsync(SampleSms);

        dispatcher.Verify(
            x => x.DispatchToAdminsAsync(
                It.Is<string>(m => m.Contains("не привязана")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessWebhookAsync_UnknownCard_DispatchesToAdmins()
    {
        var cardRepository = new Mock<ICardRepository>();
        cardRepository
            .Setup(x => x.GetByLast4Async("1062", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Card?)null);

        var dispatcher = new Mock<INotificationDispatcher>();
        var service = CreateService(cardRepository.Object, dispatcher.Object);

        await service.ProcessWebhookAsync(SampleSms);

        dispatcher.Verify(
            x => x.DispatchToAdminsAsync(
                It.Is<string>(m => m.Contains("*1062")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessWebhookAsync_DisabledCard_DispatchesToAdmins()
    {
        var cardRepository = new Mock<ICardRepository>();
        cardRepository
            .Setup(x => x.GetByLast4Async("1062", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Card { Id = 1, Last4 = "1062", DestinationChatId = -100333, Enabled = false });

        var dispatcher = new Mock<INotificationDispatcher>();
        var service = CreateService(cardRepository.Object, dispatcher.Object);

        await service.ProcessWebhookAsync(SampleSms);

        dispatcher.Verify(
            x => x.DispatchToAdminsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessWebhookAsync_UnparseableSms_DispatchesToAdmins()
    {
        var cardRepository = new Mock<ICardRepository>();
        var dispatcher = new Mock<INotificationDispatcher>();
        var service = CreateService(cardRepository.Object, dispatcher.Object);

        await service.ProcessWebhookAsync("неизвестный формат");

        dispatcher.Verify(
            x => x.DispatchToAdminsAsync(
                It.Is<string>(m => m.Contains("неизвестный формат")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        cardRepository.Verify(
            x => x.GetByLast4Async(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static SmsRoutingService CreateService(
        ICardRepository cardRepository,
        INotificationDispatcher dispatcher) =>
        new(
            new SmsParser(),
            cardRepository,
            dispatcher,
            NullLogger<SmsRoutingService>.Instance);
}