using System.Text.Json;
using System.Text.Json.Serialization;
using Orbita.Contracts;

namespace Orbita.Api.Models;

public sealed class BitrixInstanceIntegrationSettings
{
    public const bool DuplicateCheckEnabled = true;

    public string EntityType { get; set; } = "Deal";
    public int ResponsibleId { get; set; }
    public string LeadSource { get; set; } = "Авито";
    public string DealIdempotencyUfCode { get; set; } = string.Empty;
    public string DealAgeUfCode { get; set; } = "UF_CRM_1777753181424";
    public string DealProfessionUfCode { get; set; } = "UF_CRM_1777753209215";
    public string DealCityUfCode { get; set; } = "UF_CRM_1777753293892";
    public bool CheckDuplicatesInBitrix { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static BitrixInstanceIntegrationSettings FromDefaults(OrbitaBitrixSettings defaults) => new()
    {
        EntityType = defaults.EntityType,
        ResponsibleId = defaults.ResponsibleId,
        LeadSource = defaults.LeadSource,
        DealIdempotencyUfCode = defaults.DealIdempotencyUfCode,
        DealAgeUfCode = defaults.DealAgeUfCode,
        DealProfessionUfCode = defaults.DealProfessionUfCode,
        DealCityUfCode = defaults.DealCityUfCode,
        CheckDuplicatesInBitrix = DuplicateCheckEnabled
    };

    public static BitrixInstanceIntegrationSettings Parse(string? json, OrbitaBitrixSettings defaults)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return FromDefaults(defaults);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<BitrixInstanceIntegrationSettings>(json, JsonOptions)
                           ?? FromDefaults(defaults);
            parsed.CheckDuplicatesInBitrix = DuplicateCheckEnabled;
            return parsed;
        }
        catch
        {
            return FromDefaults(defaults);
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public OrbitaBitrixSettings ToOrbitaBitrixSettings() => new()
    {
        EntityType = EntityType,
        ResponsibleId = ResponsibleId,
        LeadSource = LeadSource,
        DealIdempotencyUfCode = DealIdempotencyUfCode,
        DealAgeUfCode = DealAgeUfCode,
        DealProfessionUfCode = DealProfessionUfCode,
        DealCityUfCode = DealCityUfCode,
        CheckDuplicatesInBitrix = DuplicateCheckEnabled
    };

    public BitrixInstanceIntegrationSettingsDto ToDto() => new(
        EntityType,
        ResponsibleId,
        LeadSource,
        DealIdempotencyUfCode,
        DealAgeUfCode,
        DealProfessionUfCode,
        DealCityUfCode,
        CheckDuplicatesInBitrix);

    public static BitrixInstanceIntegrationSettings FromDto(BitrixInstanceIntegrationSettingsDto? dto, OrbitaBitrixSettings defaults)
    {
        if (dto is null)
        {
            return FromDefaults(defaults);
        }

        return new BitrixInstanceIntegrationSettings
        {
            EntityType = string.IsNullOrWhiteSpace(dto.EntityType) ? defaults.EntityType : dto.EntityType,
            ResponsibleId = dto.ResponsibleId,
            LeadSource = string.IsNullOrWhiteSpace(dto.LeadSource) ? defaults.LeadSource : dto.LeadSource,
            DealIdempotencyUfCode = dto.DealIdempotencyUfCode ?? string.Empty,
            DealAgeUfCode = string.IsNullOrWhiteSpace(dto.DealAgeUfCode) ? defaults.DealAgeUfCode : dto.DealAgeUfCode,
            DealProfessionUfCode = string.IsNullOrWhiteSpace(dto.DealProfessionUfCode) ? defaults.DealProfessionUfCode : dto.DealProfessionUfCode,
            DealCityUfCode = string.IsNullOrWhiteSpace(dto.DealCityUfCode) ? defaults.DealCityUfCode : dto.DealCityUfCode,
            CheckDuplicatesInBitrix = DuplicateCheckEnabled
        };
    }
}