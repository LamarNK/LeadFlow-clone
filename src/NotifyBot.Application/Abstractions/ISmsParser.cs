using NotifyBot.Domain.Models;

namespace NotifyBot.Application.Abstractions;

/// <summary>
/// Parses raw SMS text into structured 3DS payment information.
/// </summary>
public interface ISmsParser
{
    /// <summary>
    /// Attempts to parse the SMS text. Returns null when the format is not recognized.
    /// </summary>
    SmsInfo? Parse(string rawText);
}