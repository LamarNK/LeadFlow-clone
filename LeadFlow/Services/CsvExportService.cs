using System.IO;
using System.Text;
using LeadFlow.Models;

namespace LeadFlow.Services;

public sealed class CsvExportService : ICsvExportService
{
    public async Task<string> ExportJournalAsync(IEnumerable<CandidateResponse> items, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Exports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"leadflow-journal-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("Дата/время;Аккаунт;ФИО;Телефон;Город;Вакансия;Статус;Bitrix24 Lead ID;Ошибка");
        foreach (var item in items)
        {
            sb.AppendLine(
                $"{item.CreatedAt:dd.MM.yyyy HH:mm};{Escape(item.AccountName)};{Escape(item.FullName)};{Escape(item.PhoneRaw)};{Escape(item.City)};{Escape(item.Vacancy)};{Escape(ResponseStatusFormatting.ShortLabel(item.Status))};{Escape(item.BitrixEntityId)};{Escape(item.ErrorMessage)}");
        }

        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true), cancellationToken);
        return path;
    }

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
