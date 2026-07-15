using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Orbita.Contracts;

namespace Orbita.Web.Helpers;

internal static class FormBindingHelper
{
    public static bool ReadCheckbox(IFormCollection form, string name) =>
        form.TryGetValue(name, out var values)
        && values.Count > 0
        && values.Contains("true", StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SaveBitrixLeadQuotaRequest> ParseBitrixLeadQuotas(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<BitrixLeadQuotaFormItem>>(json, WebJsonOptions)
                ?? [];
            return parsed
                .Where(x => x.BitrixInstanceId != Guid.Empty)
                .Select(x => new SaveBitrixLeadQuotaRequest(
                    x.BitrixInstanceId,
                    x.LeadExportLimit is > 0 ? x.LeadExportLimit : null))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class BitrixLeadQuotaFormItem
    {
        public Guid BitrixInstanceId { get; set; }
        public int? LeadExportLimit { get; set; }
    }
}