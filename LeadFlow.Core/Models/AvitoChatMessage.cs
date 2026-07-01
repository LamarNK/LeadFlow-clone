namespace LeadFlow.Core.Models;

/// <summary>Сообщение из мини-чата Avito (панель после клика «Перейти в чат»).</summary>
public sealed class AvitoChatMessage
{
    public string Text { get; set; } = string.Empty;

    /// <summary>ISO-8601 из атрибута <c>datetime</c> у сообщения.</summary>
    public string? At { get; set; }

    /// <summary><c>left</c> — кандидат/система, <c>right</c> — работодатель.</summary>
    public string Side { get; set; } = "left";

    /// <summary>Системное/platform-сообщение Avito (зелёные карточки, «Кандидат откликнулся…»).</summary>
    public bool IsPlatform { get; set; }
}