namespace LeadFlow.Models;

public sealed class BitrixSettings
{
    public string WebhookUrl { get; set; } = string.Empty;
    public string EntityType { get; set; } = "Deal";
    public int ResponsibleId { get; set; }
    public string LeadSource { get; set; } = "Авито";
    public bool CheckDuplicatesInBitrix { get; set; } = true;

    /// <summary>
    /// Код пользовательского поля сделки в Bitrix24 (например UF_CRM_1234567890) для идемпотентности:
    /// перед созданием сделки выполняется поиск по этому полю; при создании поле заполняется стабильным ключом
    /// вида «Источник|AccountId|SourceResponseId». Создайте в портале поле типа «строка» у сделки и укажите его REST-код.
    /// </summary>
    public string DealIdempotencyUfCode { get; set; } = string.Empty;

    /// <summary>REST-код UF поля «Возраст» у сделки (тип «число»). Пусто — не заполняется.</summary>
    public string DealAgeUfCode { get; set; } = "UF_CRM_1777750747161";

    /// <summary>REST-код UF поля «Профессия» у сделки (строка). Заполняется названием вакансии. Пусто — не заполняется.</summary>
    public string DealProfessionUfCode { get; set; } = "UF_CRM_1777753209215";

    /// <summary>REST-код UF поля «Город» у сделки (строка). Пусто — не заполняется.</summary>
    public string DealCityUfCode { get; set; } = "UF_CRM_1777753293892";
}
