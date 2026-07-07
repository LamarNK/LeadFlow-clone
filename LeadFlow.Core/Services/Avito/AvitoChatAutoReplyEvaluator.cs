using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Решает, нужен ли шаблонный ответ кандидату в мини-чате Avito.
/// </summary>
public static class AvitoChatAutoReplyEvaluator
{
    public static bool NeedsAutoReply(IReadOnlyList<AvitoChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return false;
        }

        var lastCandidateIndex = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (IsCandidateMessage(messages[i]))
            {
                lastCandidateIndex = i;
                break;
            }
        }

        if (lastCandidateIndex < 0)
        {
            return false;
        }

        for (var i = lastCandidateIndex + 1; i < messages.Count; i++)
        {
            if (IsEmployerMessage(messages[i]))
            {
                return false;
            }
        }

        return !AlreadySentDefaultAutoReply(messages);
    }

    public static bool AlreadySentDefaultAutoReply(IReadOnlyList<AvitoChatMessage> messages) =>
        messages.Any(m =>
            IsEmployerMessage(m)
            && string.Equals(
                m.Text.Trim(),
                AvitoMessengerAutoReply.DefaultMessage.Trim(),
                StringComparison.Ordinal));

    internal static bool IsCandidateMessage(AvitoChatMessage message) =>
        !message.IsPlatform
        && string.Equals(message.Side, "left", StringComparison.OrdinalIgnoreCase);

    internal static bool IsEmployerMessage(AvitoChatMessage message) =>
        !message.IsPlatform
        && string.Equals(message.Side, "right", StringComparison.OrdinalIgnoreCase);
}