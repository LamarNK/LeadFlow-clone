using LeadFlow.Core.Models;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.Avito;

public sealed record PendingChatSendDecision(
    IReadOnlyList<WorkerPendingChatMessageDto> AlreadyInChat,
    IReadOnlyList<WorkerPendingChatMessageDto> ToSend);

/// <summary>
/// Решает, какие запланированные сообщения менеджера уже есть в мини-чате Avito
/// (идемпотентный ack) и какие ещё нужно отправить.
/// </summary>
public static class AvitoPendingChatSendEvaluator
{
    public static PendingChatSendDecision Evaluate(
        IReadOnlyList<AvitoChatMessage> chat,
        IReadOnlyList<WorkerPendingChatMessageDto> pending)
    {
        if (pending.Count == 0)
        {
            return new PendingChatSendDecision([], []);
        }

        var employerMessages = chat
            .Where(AvitoChatAutoReplyEvaluator.IsEmployerMessage)
            .ToList();
        var usedEmployerMessages = new bool[employerMessages.Count];

        var already = new List<WorkerPendingChatMessageDto>();
        var toSend = new List<WorkerPendingChatMessageDto>();
        foreach (var item in pending)
        {
            var matchedIndex = FindMatchingEmployerMessage(employerMessages, usedEmployerMessages, item);
            if (matchedIndex >= 0)
            {
                usedEmployerMessages[matchedIndex] = true;
                already.Add(item);
            }
            else
            {
                toSend.Add(item);
            }
        }

        return new PendingChatSendDecision(already, toSend);
    }

    private static int FindMatchingEmployerMessage(
        IReadOnlyList<AvitoChatMessage> employerMessages,
        bool[] usedEmployerMessages,
        WorkerPendingChatMessageDto pending)
    {
        var expectedText = pending.Text.Trim();
        for (var i = 0; i < employerMessages.Count; i++)
        {
            if (usedEmployerMessages[i]
                || !string.Equals(employerMessages[i].Text.Trim(), expectedText, StringComparison.Ordinal)
                || !AvitoChatMessagesJson.TryParseMessageAtUtc(employerMessages[i].At, out var sentAtUtc)
                || sentAtUtc < pending.QueuedAtUtc)
            {
                continue;
            }

            return i;
        }

        return -1;
    }
}
