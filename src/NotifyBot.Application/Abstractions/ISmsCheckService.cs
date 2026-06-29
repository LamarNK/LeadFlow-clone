namespace NotifyBot.Application.Abstractions;

/// <summary>
/// On-demand SMS check: fetches recent messages from Plusofon and returns 3DS codes.
/// </summary>
public interface ISmsCheckService
{
    /// <summary>
    /// Fetches recent SMS, parses 3DS codes, and formats a reply for the requesting chat.
    /// When the chat has bound cards, only matching codes are returned.
    /// </summary>
    Task<string> CheckAsync(long requestingChatId, CancellationToken cancellationToken = default);

    /// <summary>Admin debug: lists recent SMS from Plusofon (raw, all directions).</summary>
    Task<string> ListAllForDebugAsync(CancellationToken cancellationToken = default);
}