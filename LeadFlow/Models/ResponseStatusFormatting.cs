namespace LeadFlow.Models;

public static class ResponseStatusFormatting
{
    /// <summary>Краткая подпись для списков и фильтров.</summary>
    public static string ShortLabel(ResponseStatus status) => status switch
    {
        ResponseStatus.New => "Новый",
        ResponseStatus.InProgress => "В обработке",
        ResponseStatus.Sent => "В CRM",
        ResponseStatus.Duplicate => "Дубль",
        ResponseStatus.Error => "Ошибка",
        ResponseStatus.ActionRequired => "Нужны действия",
        _ => status.ToString()
    };

    /// <summary>Развёрнутое описание в панели деталей.</summary>
    public static string DetailDescription(ResponseStatus status) => status switch
    {
        ResponseStatus.New => "Новый отклик",
        ResponseStatus.InProgress => "В обработке",
        ResponseStatus.Sent => "Отправлен в Bitrix24",
        ResponseStatus.Duplicate => "Найден дубль",
        ResponseStatus.Error => "Ошибка обработки",
        ResponseStatus.ActionRequired => "Нужно действие",
        _ => ShortLabel(status)
    };
}
