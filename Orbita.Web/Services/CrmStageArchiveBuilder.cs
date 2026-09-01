using System.Globalization;
using System.IO.Compression;
using System.Text;
using Orbita.Contracts;

namespace Orbita.Web.Services;

public sealed record CrmStageArchiveResult(
    Stream Stream,
    string FileName,
    int CardCount);

public static class CrmStageArchiveBuilder
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public static CrmStageArchiveResult Build(
        string officeName,
        string stage,
        IReadOnlyCollection<CrmCandidateCardDto> cards,
        DateTime generatedAtUtc)
    {
        var generatedUtc = generatedAtUtc.Kind == DateTimeKind.Utc
            ? generatedAtUtc
            : generatedAtUtc.ToUniversalTime();
        var orderedCards = cards
            .OrderByDescending(card => card.CreatedAtUtc)
            .ThenBy(card => card.Id)
            .ToList();
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            WriteCards(archive, orderedCards);
        }

        stream.Position = 0;
        var timestamp = generatedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"CRM-{SafeFilePart(officeName)}-{SafeFilePart(stage)}-{timestamp}.zip";
        return new CrmStageArchiveResult(stream, fileName, orderedCards.Count);
    }

    private static void WriteCards(ZipArchive archive, IReadOnlyList<CrmCandidateCardDto> cards)
    {
        using var writer = CreateWriter(archive, "Сделки.csv");
        var rows = cards
            .Select(card => (Card: card, Phones: GetPhones(card)))
            .ToList();
        var phoneColumnCount = Math.Max(1, rows.Select(row => row.Phones.Count).DefaultIfEmpty(0).Max());
        var header = new List<object?>
        {
            "ФИО", "Возраст", "Гражданство", "Город", "Вакансия",
            "Этап"
        };
        for (var index = 1; index <= phoneColumnCount; index++) header.Add($"Телефон {index}");
        header.AddRange(
        [
            "Ответственный", "В активной нагрузке", "Аккаунт/источник", "Ссылка источника",
            "Ссылка вакансии", "Мессенджер", "Открытых задач", "Есть просроченная задача"
        ]);
        WriteCsvRow(writer, header);

        foreach (var row in rows)
        {
            var card = row.Card;
            var cells = new List<object?>
            {
                card.FullName,
                card.Age,
                card.Citizenship,
                card.City,
                card.Vacancy,
                card.Stage
            };
            for (var index = 0; index < phoneColumnCount; index++) cells.Add(PhoneAt(row.Phones, index));
            cells.AddRange(
            [
                card.ManagerName,
                YesNo(card.IsInActiveLoad),
                card.AccountName,
                card.SourceUrl,
                card.VacancyUrl,
                card.MessengerUrl,
                card.OpenTaskCount,
                YesNo(card.HasOverdueTask)
            ]);
            WriteCsvRow(writer, cells);
        }
    }

    private static List<string> GetPhones(CrmCandidateCardDto card)
    {
        var phones = new List<string>();
        AddPhone(card.PhoneRaw);
        foreach (var phone in card.ContactPhones ?? []) AddPhone(phone);
        return phones;

        void AddPhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return;
            var normalized = phone.Trim();
            if (!phones.Contains(normalized, StringComparer.Ordinal)) phones.Add(normalized);
        }
    }

    private static object? PhoneAt(IReadOnlyList<string> phones, int index) =>
        index < phones.Count ? phones[index] : null;

    private static string YesNo(bool value) => value ? "Да" : "Нет";

    private static StreamWriter CreateWriter(ZipArchive archive, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        return new StreamWriter(entry.Open(), Utf8WithBom, bufferSize: 16 * 1024, leaveOpen: false);
    }

    private static void WriteCsvRow(TextWriter writer, IReadOnlyList<object?> cells)
    {
        for (var index = 0; index < cells.Count; index++)
        {
            if (index > 0) writer.Write(';');
            var value = FormatCell(cells[index]);
            writer.Write('"');
            writer.Write(value.Replace("\"", "\"\"", StringComparison.Ordinal));
            writer.Write('"');
        }

        writer.Write("\r\n");
    }

    private static string FormatCell(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTime date => date.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset date => date.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };

        // Excel must not execute user-controlled names, cities or vacancies as formulas.
        return text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? $"'{text}"
            : text;
    }

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((value ?? string.Empty)
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray())
            .Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "Без-названия" : cleaned;
    }
}
