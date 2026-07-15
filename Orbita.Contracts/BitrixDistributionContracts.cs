namespace Orbita.Contracts;

public sealed record BitrixInstanceIntegrationSettingsDto(
    string EntityType,
    int ResponsibleId,
    string LeadSource,
    string DealIdempotencyUfCode,
    string DealAgeUfCode,
    string DealProfessionUfCode,
    string DealCityUfCode,
    bool CheckDuplicatesInBitrix);

public sealed record BitrixInstanceDto(
    Guid Id,
    Guid OfficeId,
    string Name,
    string Signature,
    string? MaskedWebhookUrl,
    string? PortalHost,
    string ValidationStatus,
    string? ValidationMessage,
    DateTime? LastValidatedAtUtc,
    bool IsEnabled,
    BitrixInstanceIntegrationSettingsDto IntegrationSettings,
    int? LeadExportLimit,
    int LeadExportSessionCount,
    DateTime? LeadExportSessionStartedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record BitrixInstanceListItemDto(
    Guid Id,
    string Name,
    string Signature,
    string? PortalHost,
    string ValidationStatus,
    string? ValidationMessage,
    bool IsEnabled,
    int? LeadExportLimit = null,
    int LeadExportSessionCount = 0);

public sealed record CreateBitrixInstanceRequest(
    string Name,
    string Signature,
    string WebhookUrl,
    BitrixInstanceIntegrationSettingsDto? IntegrationSettings,
    bool IsEnabled = true);

public sealed record UpdateBitrixInstanceRequest(
    string Name,
    string Signature,
    string? WebhookUrl,
    BitrixInstanceIntegrationSettingsDto? IntegrationSettings,
    bool IsEnabled);

public sealed record ValidateBitrixInstanceRequest(string? WebhookUrl);

public sealed record DistributionNodeDto(
    Guid Id,
    Guid? ParentNodeId,
    Guid BitrixInstanceId,
    string BitrixName,
    string BitrixSignature,
    int SortOrder,
    double EditorPositionX,
    double EditorPositionY,
    bool HasChildren);

public sealed record DistributionRouteDto(
    Guid Id,
    Guid OfficeId,
    bool IsAutoDistributionEnabled,
    IReadOnlyList<DistributionNodeDto> Nodes,
    DateTime? UpdatedAtUtc);

public sealed record SaveBitrixLeadQuotaRequest(
    Guid BitrixInstanceId,
    int? LeadExportLimit = null);

public sealed record SaveDistributionRouteRequest(
    bool IsAutoDistributionEnabled,
    IReadOnlyList<SaveDistributionNodeRequest> Nodes,
    IReadOnlyList<SaveBitrixLeadQuotaRequest>? BitrixLeadQuotas = null);

public sealed record SaveDistributionNodeRequest(
    Guid? Id,
    Guid? ParentNodeId,
    Guid BitrixInstanceId,
    int SortOrder,
    double EditorPositionX,
    double EditorPositionY);

public sealed record SendResponseToBitrixRequest(Guid BitrixInstanceId);