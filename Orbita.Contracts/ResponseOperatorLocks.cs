namespace Orbita.Contracts;

/// <summary>
/// Поля отклика, которые оператор правил вручную. Воркер/Avito их больше не затирает.
/// </summary>
public static class ResponseOperatorLocks
{
    public const string City = "city";
    public const string Vacancy = "vacancy";
    public const string VacancyUrl = "vacancyUrl";
    public const string SourceUrl = "sourceUrl";
    public const string Age = "age";
    public const string Gender = "gender";
    public const string Citizenship = "citizenship";
    public const string MessengerUrl = "messengerUrl";
    public const string FullName = "fullName";

    public static bool Contains(string? packed, string field)
    {
        if (string.IsNullOrWhiteSpace(packed) || string.IsNullOrWhiteSpace(field))
        {
            return false;
        }

        foreach (var part in packed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(part, field, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string Add(string? packed, string field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return packed ?? string.Empty;
        }

        if (Contains(packed, field))
        {
            return packed ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(packed))
        {
            return field.Trim();
        }

        var parts = packed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(field.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase);
        return string.Join(',', parts);
    }
}
