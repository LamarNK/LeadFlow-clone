using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

/// <summary>Шаблонный автоответ в мини-чат Avito, если кандидат написал и работодатель ещё не ответил.</summary>
public static class AvitoMessengerAutoReply
{
    public const string DefaultMessage = AvitoMessengerAutoReplySettings.DefaultMessage;
}