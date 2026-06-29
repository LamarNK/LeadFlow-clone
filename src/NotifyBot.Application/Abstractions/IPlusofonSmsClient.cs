namespace NotifyBot.Application.Abstractions;

public sealed record PlusofonSmsMessage(
    string Text,
    DateTimeOffset? ReceivedAtUtc,
    bool Incoming,
    string? Sender = null,
    string? Receiver = null);

/// <summary>
/// Fetches SMS messages from the Plusofon REST API.
/// </summary>
public interface IPlusofonSmsClient
{
    Task<IReadOnlyList<PlusofonSmsMessage>> GetRecentIncomingAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlusofonSmsMessage>> GetRecentAsync(
        int limit,
        bool? incomingOnly,
        CancellationToken cancellationToken = default);
}