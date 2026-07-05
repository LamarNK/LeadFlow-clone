using System.Text;
using System.Text.Json;

namespace LeadFlow.Core.Services.Avito;

/// <summary>Статистика одного прохода извлечения откликов со страницы Avito.</summary>
public sealed record AvitoCandidatesExtractionSummary(
    string? PageUrl,
    string PageVariant,
    int DomItemCount,
    int DomStatusCount,
    int ScriptCandidatesCount,
    int ParsedValidCount,
    int SkippedExistingSourceId,
    int SkippedDuplicatePhoneInDb,
    int SkippedDuplicatePhoneInBatch,
    int NewUniqueCount,
    IReadOnlyList<string> SampleNames)
{
    public string PageVariantLabel => DescribePageVariant(PageUrl, PageVariant);

    public static AvitoCandidatesExtractionSummary Empty { get; } = new(
        null,
        "unknown",
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        []);

    public static int ReadScriptCandidatesCount(JsonElement root) =>
        root.TryGetProperty("candidates", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.GetArrayLength()
            : 0;

    public static string DescribePageVariant(string? pageUrl, string? pageVariant)
    {
        if (!string.IsNullOrWhiteSpace(pageUrl))
        {
            if (pageUrl.Contains("/profile/job/responses", StringComparison.OrdinalIgnoreCase))
            {
                return "CRM (/profile/job/responses)";
            }

            if (pageUrl.Contains("/profile/candidates", StringComparison.OrdinalIgnoreCase))
            {
                return "классическая (/profile/candidates)";
            }
        }

        return pageVariant?.Trim().ToLowerInvariant() switch
        {
            "job-crm" => "CRM (/profile/job/responses)",
            "legacy" => "классическая (/profile/candidates)",
            _ => string.IsNullOrWhiteSpace(pageUrl) ? "не определено" : $"неизвестный URL ({pageUrl})"
        };
    }

    public string FormatLogLine()
    {
        var sb = new StringBuilder();
        sb.Append($"страница {PageVariantLabel}");
        if (!string.IsNullOrWhiteSpace(PageUrl))
        {
            sb.Append($" · {PageUrl.Trim()}");
        }

        sb.Append($"; в DOM {DomItemCount} карточек");
        if (DomStatusCount > 0)
        {
            sb.Append($" ({DomStatusCount} со статусом)");
        }

        sb.Append($"; скрипт извлёк {ScriptCandidatesCount}, валидных {ParsedValidCount}");

        if (ScriptCandidatesCount > ParsedValidCount)
        {
            sb.Append($" (отброшено без имени/телефона: {ScriptCandidatesCount - ParsedValidCount})");
        }

        if (DomItemCount > ParsedValidCount && DomItemCount > 0)
        {
            sb.Append($"; в DOM больше карточек, чем валидных — возможно неполный скролл");
        }

        var filtered = SkippedExistingSourceId + SkippedDuplicatePhoneInDb + SkippedDuplicatePhoneInBatch;
        if (filtered > 0)
        {
            sb.Append($"; отфильтровано {filtered}");
            var parts = new List<string>();
            if (SkippedExistingSourceId > 0)
            {
                parts.Add($"{SkippedExistingSourceId} уже по sourceResponseId (дедуп lookup)");
            }

            if (SkippedDuplicatePhoneInDb > 0)
            {
                parts.Add($"{SkippedDuplicatePhoneInDb} дубль по телефону (дедуп lookup)");
            }

            if (SkippedDuplicatePhoneInBatch > 0)
            {
                parts.Add($"{SkippedDuplicatePhoneInBatch} повтор в этой выборке");
            }

            sb.Append($" ({string.Join(", ", parts)})");
        }

        sb.Append($"; новых к публикации {NewUniqueCount}");

        if (SampleNames.Count > 0)
        {
            sb.Append(": ");
            sb.Append(string.Join(", ", SampleNames));
            if (NewUniqueCount > SampleNames.Count)
            {
                sb.Append($" и ещё {NewUniqueCount - SampleNames.Count}");
            }
        }

        return sb.ToString();
    }
}