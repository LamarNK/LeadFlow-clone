namespace LeadFlow.Services.Bitrix;

public enum BitrixDuplicateLookupOutcome
{
    /// <summary>Дублей по телефону в Bitrix24 не найдено.</summary>
    NoDuplicate,

    /// <summary>Найден существующий контакт/сущность с таким телефоном.</summary>
    Duplicate,

    /// <summary>Запрос к Bitrix24 не выполнен или ответ невалиден — внешняя проверка недоступна.</summary>
    Unavailable,

    /// <summary>Проверка в Bitrix24 не выполнялась (нет webhook или телефона для запроса).</summary>
    Skipped
}

public readonly record struct BitrixDuplicateLookupResult(
    BitrixDuplicateLookupOutcome Outcome,
    string? ErrorMessage = null)
{
    public bool IsDuplicate => Outcome == BitrixDuplicateLookupOutcome.Duplicate;

    public bool IsUnavailable => Outcome == BitrixDuplicateLookupOutcome.Unavailable;

    public bool IsSkipped => Outcome == BitrixDuplicateLookupOutcome.Skipped;
}
