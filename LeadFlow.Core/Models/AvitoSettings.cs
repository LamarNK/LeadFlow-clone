using System.Collections.ObjectModel;

namespace LeadFlow.Core.Models;

public sealed class AvitoSettings
{
    public ObservableCollection<AvitoAccount> Accounts { get; set; } = new();
    public AvitoMessengerAutoReplySettings MessengerAutoReply { get; set; } = new();
}

public sealed class AvitoMessengerAutoReplySettings
{
    public const string DefaultMessage =
        "Доброго времени суток, наши коллеги с вами свяжутся и поподробнее расскажут о вакансии.";

    public bool Enabled { get; set; } = true;
    public string Message { get; set; } = DefaultMessage;

    public AvitoMessengerAutoReplySettings Clone() => new()
    {
        Enabled = Enabled,
        Message = Message
    };
}
