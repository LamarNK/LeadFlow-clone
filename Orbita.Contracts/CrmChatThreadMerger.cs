namespace Orbita.Contracts;

/// <summary>
/// Склеивает снимок чата Avito с очередью исходящих менеджера:
/// sent совпадает с исходящим Avito по тексту (без дубля), planned дописывается в конец.
/// </summary>
public static class CrmChatThreadMerger
{
    public static IReadOnlyList<CrmChatMessageDto> Merge(
        IReadOnlyList<CrmChatMessageDto> avitoMessages,
        IReadOnlyList<CrmChatMessageDto> outboundMessages)
    {
        if (outboundMessages.Count == 0)
        {
            return avitoMessages;
        }

        var sent = new List<CrmChatMessageDto>();
        var planned = new List<CrmChatMessageDto>();
        foreach (var message in outboundMessages)
        {
            if (string.Equals(message.Status, CrmOutboundChatStatuses.Planned, StringComparison.Ordinal)
                || string.Equals(message.Status, CrmOutboundChatStatuses.Sending, StringComparison.Ordinal))
            {
                planned.Add(message);
            }
            else if (string.Equals(message.Status, CrmOutboundChatStatuses.Sent, StringComparison.Ordinal))
            {
                sent.Add(message);
            }
        }

        var usedSent = new bool[sent.Count];
        var merged = new List<CrmChatMessageDto>(avitoMessages.Count + outboundMessages.Count);
        foreach (var avito in avitoMessages)
        {
            if (string.Equals(avito.Tone, "outgoing", StringComparison.OrdinalIgnoreCase))
            {
                var match = FindUnusedSent(sent, usedSent, avito.Text);
                if (match is not null)
                {
                    merged.Add(match);
                    continue;
                }
            }

            merged.Add(avito);
        }

        for (var i = 0; i < sent.Count; i++)
        {
            if (!usedSent[i])
            {
                merged.Add(sent[i]);
            }
        }

        merged.AddRange(planned);
        return merged;
    }

    private static CrmChatMessageDto? FindUnusedSent(
        IReadOnlyList<CrmChatMessageDto> sent,
        bool[] used,
        string text)
    {
        var expected = text.Trim();
        for (var i = 0; i < sent.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            if (string.Equals(sent[i].Text.Trim(), expected, StringComparison.Ordinal))
            {
                used[i] = true;
                return sent[i];
            }
        }

        return null;
    }
}
