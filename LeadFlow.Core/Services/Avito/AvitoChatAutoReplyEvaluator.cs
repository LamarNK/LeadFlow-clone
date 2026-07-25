using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Решает, нужен ли шаблонный ответ кандидату в мини-чате Avito.
/// </summary>
public static class AvitoChatAutoReplyEvaluator
{
    public static bool NeedsAutoReply(IReadOnlyList<AvitoChatMessage> messages) =>
        NeedsAutoReply(messages, AvitoMessengerAutoReply.DefaultMessage);

    public static bool NeedsAutoReply(
        IReadOnlyList<AvitoChatMessage> messages,
        string autoReplyMessage)
    {
        if (messages.Count == 0 || string.IsNullOrWhiteSpace(autoReplyMessage))
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

        return !AlreadySentAutoReply(messages, autoReplyMessage);
    }

    public static bool AlreadySentDefaultAutoReply(IReadOnlyList<AvitoChatMessage> messages) =>
        AlreadySentAutoReply(messages, AvitoMessengerAutoReply.DefaultMessage);

    public static bool AlreadySentAutoReply(
        IReadOnlyList<AvitoChatMessage> messages,
        string autoReplyMessage) =>
        messages.Any(m =>
            IsEmployerMessage(m)
            && string.Equals(
                m.Text.Trim(),
                autoReplyMessage.Trim(),
                StringComparison.Ordinal));

    internal static bool IsCandidateMessage(AvitoChatMessage message) =>
        !message.IsPlatform
        && string.Equals(message.Side, "left", StringComparison.OrdinalIgnoreCase);

    internal static bool IsEmployerMessage(AvitoChatMessage message) =>
        !message.IsPlatform
        && string.Equals(message.Side, "right", StringComparison.OrdinalIgnoreCase);
}