using NotifyBot.Domain.Entities;

namespace NotifyBot.Infrastructure.Data;

internal static class CardSeedData
{
    public static readonly Card[] Cards =
    [
        new() { Id = 1, Last4 = "1062", Label = "Office3", Enabled = true },
        new() { Id = 2, Last4 = "9669", Label = "Office1", Enabled = true },
        new() { Id = 3, Last4 = "3098", Label = "Office2", Enabled = true }
    ];
}