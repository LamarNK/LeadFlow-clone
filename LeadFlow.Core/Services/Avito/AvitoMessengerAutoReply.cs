namespace LeadFlow.Core.Services.Avito;

/// <summary>Шаблонный автоответ в мини-чат Avito, если кандидат написал и работодатель ещё не ответил.</summary>
public static class AvitoMessengerAutoReply
{
    public const bool Enabled = true;

    public const string DefaultMessage =
        "Доброго времени суток, наши коллеги с вами свяжутся и поподробнее расскажут о вакансии.";
}