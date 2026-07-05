using Microsoft.Extensions.Options;
using NotifyBot.Application.Models;
using NotifyBot.Application.Options;
using NotifyBot.Infrastructure.Telegram;

namespace NotifyBot.Tests;

public sealed class SmsWatchCoordinatorTests
{
    [Fact]
    public void TryRegister_SecondCallForSameChat_ExtendsWithoutDuplicateSession()
    {
        var coordinator = CreateCoordinator(durationMinutes: 5);
        var first = coordinator.TryRegister(-100333);
        var second = coordinator.TryRegister(-100333);

        Assert.Equal(SmsWatchRegistrationKind.Started, first.Kind);
        Assert.Equal(SmsWatchRegistrationKind.AlreadyActive, second.Kind);
        Assert.True(second.ExpiresAtUtc >= first.ExpiresAtUtc);
        Assert.Single(coordinator.GetActiveQueries());
    }

    [Fact]
    public void GetActiveQueries_ReturnsOnlyNonExpiredSessions()
    {
        var coordinator = CreateCoordinator(durationMinutes: 1);
        coordinator.TryRegister(-100111);
        coordinator.TryRegister(-100222);

        Assert.Equal(2, coordinator.GetActiveQueries().Count);
        Assert.Empty(coordinator.TakeExpiredChatIds());
    }

    [Fact]
    public void MarkDelivered_ExcludesMessageOnNextQuery()
    {
        var coordinator = CreateCoordinator(durationMinutes: 5);
        coordinator.TryRegister(-100333);
        coordinator.MarkDelivered(-100333, "sms-text");

        var query = Assert.Single(coordinator.GetActiveQueries());
        Assert.Contains("sms-text", query.ExcludeKeys);
    }

    private static SmsWatchCoordinator CreateCoordinator(int durationMinutes) =>
        new(Options.Create(new PlusofonOptions
        {
            SmsWatchDurationMinutes = durationMinutes,
            SmsWatchPollIntervalSeconds = 5,
            SmsWatchIdleIntervalSeconds = 15
        }));
}