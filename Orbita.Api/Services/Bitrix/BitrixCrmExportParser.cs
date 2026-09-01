using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Orbita.Api.Services.Bitrix;

public sealed class BitrixCrmExportParser
{
    public const long MaxFileBytes = 25 * 1024 * 1024;

    private static readonly Regex HtmlRowRegex = new(
        @"<tr\b[^>]*>(.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlCellRegex = new(
        @"<t[hd]\b[^>]*>(.*?)</t[hd]>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlBreakRegex = new(
        @"<br\s*/?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] DateFormats =
    [
        "dd.MM.yyyy HH:mm:ss",
        "dd.MM.yyyy H:mm:ss",
        "dd.MM.yyyy HH:mm",
        "dd.MM.yyyy H:mm",
        "dd.MM.yyyy",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd"
    ];

    public async Task<BitrixImportSnapshot> ParseAsync(
        Stream source,
        string fileName,
        IReadOnlyCollection<string> requestedStageNames,
        CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not ".xls" and not ".xlsx")
        {
            throw new InvalidDataException("Поддерживаются только выгрузки Bitrix24 в форматах .xls и .xlsx.");
        }

        await using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
        {
            throw new InvalidDataException("Файл пуст.");
        }

        if (buffer.Length > MaxFileBytes)
        {
            throw new InvalidDataException("Файл слишком большой. Максимальный размер — 25 МБ.");
        }

        buffer.Position = 0;
        var rows = extension == ".xlsx"
            ? ReadXlsxRows(buffer)
            : ReadHtmlRows(buffer);
        return BuildSnapshot(rows, requestedStageNames);
    }

    private static BitrixImportSnapshot BuildSnapshot(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
        IReadOnlyCollection<string> requestedStageNames)
    {
        var requested = requestedStageNames
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
        {
            throw new InvalidDataException("Выберите стадию, которую нужно импортировать из файла.");
        }

        if (rows.Count == 0)
        {
            throw new InvalidDataException("В файле не найдены строки сделок.");
        }

        RequireColumn(rows, "ID");
        RequireColumn(rows, "Стадия сделки");
        var phoneColumns = new[]
        {
            "Контакт: Рабочий телефон",
            "Контакт: Мобильный телефон",
            "Контакт: Домашний телефон",
            "Контакт: Телефон для рассылок",
            "Контакт: Другой телефон"
        };
        if (!phoneColumns.Any(column => rows[0].ContainsKey(column)))
        {
            throw new InvalidDataException("В выгрузке отсутствуют телефонные поля контакта Bitrix24.");
        }

        var selected = rows
            .Where(row => requested.Contains(Get(row, "Стадия сделки")))
            .ToList();
        if (selected.Count == 0)
        {
            throw new InvalidDataException(
                "В файле нет сделок на выбранной стадии: " + string.Join(", ", requested));
        }

        var duplicateDealId = selected
            .Select(row => ParseLong(Get(row, "ID")))
            .Where(id => id > 0)
            .GroupBy(id => id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDealId is not null)
        {
            throw new InvalidDataException($"В файле сделка #{duplicateDealId.Key} встречается несколько раз.");
        }

        var responsibleNames = selected
            .Select(row => Get(row, "Ответственный"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var responsibleIds = responsibleNames
            .Select((name, index) => new { Name = name, Id = (long)index + 1 })
            .ToDictionary(item => item.Name, item => item.Id, StringComparer.OrdinalIgnoreCase);
        var users = responsibleIds.ToDictionary(
            item => item.Value,
            item => new BitrixImportUser(item.Value, item.Key));

        var stageNames = selected
            .Select(row => Get(row, "Стадия сделки"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var stageIds = stageNames
            .Select((name, index) => new { Name = name, Id = $"FILE_STAGE_{index + 1}" })
            .ToDictionary(item => item.Name, item => item.Id, StringComparer.OrdinalIgnoreCase);
        var stages = stageIds
            .Select(item => new BitrixImportStage(item.Value, item.Key))
            .ToList();
        var fieldTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FILE_AGE"] = "Возраст",
            ["FILE_PROFESSION"] = "Профессия",
            ["FILE_CITY"] = "Город"
        };

        var deals = new List<BitrixImportDeal>(selected.Count);
        foreach (var row in selected)
        {
            var dealId = ParseLong(Get(row, "ID"));
            if (dealId <= 0)
            {
                throw new InvalidDataException("В одной из строк отсутствует корректный ID сделки Bitrix24.");
            }

            var stageName = Get(row, "Стадия сделки");
            var responsibleName = Get(row, "Ответственный");
            var fullName = JoinName(
                Get(row, "Контакт: Фамилия"),
                Get(row, "Контакт: Имя"),
                Get(row, "Контакт: Отчество"));
            fullName = FirstNonEmpty(fullName, Get(row, "Контакт"), Get(row, "Название сделки"), $"Кандидат {dealId}");
            var phone = FirstNonEmpty(phoneColumns.Select(column => Get(row, column)).ToArray());
            var createdAtUtc = ParseBitrixDate(Get(row, "Дата создания")) ?? DateTime.UtcNow;
            var updatedAtUtc = ParseBitrixDate(Get(row, "Дата изменения")) ?? createdAtUtc;
            var contactId = ParseLong(Get(row, "Контакт: ID"));
            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["FILE_AGE"] = NullIfEmpty(Get(row, "Возраст")),
                ["FILE_PROFESSION"] = NullIfEmpty(Get(row, "Профессия")),
                ["FILE_CITY"] = NullIfEmpty(Get(row, "Город"))
            };
            var contactFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["WORK_PHONE"] = NullIfEmpty(Get(row, "Контакт: Рабочий телефон")),
                ["MOBILE_PHONE"] = NullIfEmpty(Get(row, "Контакт: Мобильный телефон"))
            };

            deals.Add(new BitrixImportDeal(
                dealId,
                FirstNonEmpty(Get(row, "Название сделки"), fullName),
                stageIds[stageName],
                stageName,
                responsibleIds.TryGetValue(responsibleName, out var responsibleId) ? responsibleId : null,
                createdAtUtc,
                updatedAtUtc,
                Get(row, "Комментарий"),
                fields,
                new BitrixImportContact(contactId, fullName, phone, contactFields),
                [],
                []));
        }

        return new BitrixImportSnapshot(stages, fieldTitles, users, deals);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadHtmlRows(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var html = reader.ReadToEnd();
        var table = HtmlRowRegex.Matches(html)
            .Select(match => HtmlCellRegex.Matches(match.Groups[1].Value)
                .Select(cell => DecodeHtmlCell(cell.Groups[1].Value))
                .ToArray())
            .Where(row => row.Length > 0)
            .ToList();
        return BuildRows(table);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadXlsxRows(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var workbook = LoadXml(archive, "xl/workbook.xml");
        XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var firstSheet = workbook.Descendants().FirstOrDefault(element => element.Name.LocalName == "sheet")
                         ?? throw new InvalidDataException("В книге Excel не найден лист.");
        var relationshipId = firstSheet.Attribute(relationships + "id")?.Value
                             ?? throw new InvalidDataException("Не удалось определить первый лист Excel.");
        var workbookRelationships = LoadXml(archive, "xl/_rels/workbook.xml.rels");
        var target = workbookRelationships.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Relationship"
                                       && string.Equals(
                                           element.Attribute("Id")?.Value,
                                           relationshipId,
                                           StringComparison.Ordinal))
            ?.Attribute("Target")?.Value
            ?? throw new InvalidDataException("Не удалось открыть первый лист Excel.");
        var sheetPath = target.StartsWith("/", StringComparison.Ordinal)
            ? target.TrimStart('/')
            : "xl/" + target.TrimStart('/');
        var worksheet = LoadXml(archive, NormalizeZipPath(sheetPath));
        var sharedStringsEntry = archive.GetEntry("xl/sharedStrings.xml");
        var sharedStrings = sharedStringsEntry is null
            ? []
            : LoadXml(sharedStringsEntry).Descendants()
                .Where(element => element.Name.LocalName == "si")
                .Select(element => string.Concat(
                    element.Descendants().Where(child => child.Name.LocalName == "t").Select(child => child.Value)))
                .ToList();
        var table = new List<string[]>();
        foreach (var row in worksheet.Descendants().Where(element => element.Name.LocalName == "row"))
        {
            var values = new SortedDictionary<int, string>();
            foreach (var cell in row.Elements().Where(element => element.Name.LocalName == "c"))
            {
                var reference = cell.Attribute("r")?.Value ?? string.Empty;
                var index = ColumnIndex(reference);
                if (index < 0)
                {
                    continue;
                }

                var type = cell.Attribute("t")?.Value;
                var raw = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ?? string.Empty;
                var value = type switch
                {
                    "s" when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedIndex)
                             && sharedIndex >= 0 && sharedIndex < sharedStrings.Count => sharedStrings[sharedIndex],
                    "inlineStr" => string.Concat(
                        cell.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value)),
                    _ => raw
                };
                values[index] = value.Trim();
            }

            if (values.Count > 0)
            {
                var result = new string[values.Keys.Max() + 1];
                foreach (var value in values)
                {
                    result[value.Key] = value.Value;
                }

                table.Add(result);
            }
        }

        return BuildRows(table);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> BuildRows(IReadOnlyList<string[]> table)
    {
        if (table.Count < 2)
        {
            return [];
        }

        var headers = table[0].Select(value => value.Trim()).ToArray();
        var result = new List<IReadOnlyDictionary<string, string>>();
        foreach (var sourceRow in table.Skip(1))
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < headers.Length; index++)
            {
                if (!string.IsNullOrWhiteSpace(headers[index]))
                {
                    row[headers[index]] = index < sourceRow.Length ? sourceRow[index].Trim() : string.Empty;
                }
            }

            if (row.Values.Any(value => !string.IsNullOrWhiteSpace(value)))
            {
                result.Add(row);
            }
        }

        return result;
    }

    private static XDocument LoadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path)
                    ?? throw new InvalidDataException($"В книге Excel отсутствует {path}.");
        return LoadXml(entry);
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var reader = entry.Open();
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string NormalizeZipPath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        var hasLetters = false;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch))
            {
                break;
            }

            hasLetters = true;
            index = checked(index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1));
        }

        return hasLetters ? index - 1 : -1;
    }

    private static string DecodeHtmlCell(string value)
    {
        var withBreaks = HtmlBreakRegex.Replace(value, "\n");
        var withoutTags = HtmlTagRegex.Replace(withBreaks, string.Empty);
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private static void RequireColumn(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
        string column)
    {
        if (!rows[0].ContainsKey(column))
        {
            throw new InvalidDataException($"В выгрузке отсутствует обязательная колонка «{column}».");
        }
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string column) =>
        row.TryGetValue(column, out var value) ? value.Trim() : string.Empty;

    private static string JoinName(params string[] parts) =>
        string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.Trim()));

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long ParseLong(string value) =>
        long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;

    private static DateTime? ParseBitrixDate(string value)
    {
        if (!DateTime.TryParseExact(
                value.Trim(),
                DateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var local))
        {
            return null;
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        try
        {
            var timezone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Yekaterinburg");
            return TimeZoneInfo.ConvertTimeToUtc(local, timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            try
            {
                var timezone = TimeZoneInfo.FindSystemTimeZoneById("Ekaterinburg Standard Time");
                return TimeZoneInfo.ConvertTimeToUtc(local, timezone);
            }
            catch (TimeZoneNotFoundException)
            {
                return DateTime.SpecifyKind(local.AddHours(-5), DateTimeKind.Utc);
            }
        }
    }
}
