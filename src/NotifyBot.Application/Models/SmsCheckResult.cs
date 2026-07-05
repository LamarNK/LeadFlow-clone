namespace NotifyBot.Application.Models;

public sealed record SmsCheckResult(string Reply, bool ShouldStartWatch);