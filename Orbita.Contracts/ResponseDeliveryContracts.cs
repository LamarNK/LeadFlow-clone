namespace Orbita.Contracts;

public static class ResponseCrmDeliveryOutcomes
{
    public const string Sent = "Sent";
    public const string Duplicate = "Duplicate";
    public const string Error = "Error";
    public const string Unavailable = "Unavailable";
}

/// <summary>Manual or auto multi-channel delivery of a collected response.</summary>
public sealed record DeliverResponseRequest(
    Guid? OfficeId = null,
    bool ToCrm = true,
    bool ToBitrix = false,
    Guid? BitrixInstanceId = null,
    /// <summary>When ToBitrix and no instance — use office distribution route.</summary>
    bool UseBitrixRoute = true,
    /// <summary>Multiple CRM target offices (preferred over <see cref="OfficeId"/>).</summary>
    IReadOnlyList<Guid>? OfficeIds = null,
    /// <summary>Multiple Bitrix portals (preferred over <see cref="BitrixInstanceId"/>). Empty + ToBitrix → route.</summary>
    IReadOnlyList<Guid>? BitrixInstanceIds = null);

public sealed record DeliverResponseChannelResultDto(
    string Channel,
    bool Success,
    string Outcome,
    string? ErrorMessage,
    Guid? CardId = null,
    Guid? BitrixInstanceId = null,
    string? BitrixEntityId = null);

public sealed record DeliverResponseResultDto(
    bool Success,
    string Status,
    string? ErrorMessage,
    Guid? OfficeId,
    IReadOnlyList<DeliverResponseChannelResultDto> Channels);

public sealed record BulkDeliverResponsesRequest(
    IReadOnlyList<Guid> ResponseIds,
    Guid? OfficeId = null,
    bool ToCrm = true,
    bool ToBitrix = false,
    Guid? BitrixInstanceId = null,
    bool UseBitrixRoute = true,
    IReadOnlyList<Guid>? OfficeIds = null,
    IReadOnlyList<Guid>? BitrixInstanceIds = null);

public sealed record BulkDeliverItemResultDto(
    Guid ResponseId,
    bool Success,
    string Status,
    string? ErrorMessage);

public sealed record BulkDeliverResponsesResultDto(
    int Total,
    int Succeeded,
    int Failed,
    IReadOnlyList<BulkDeliverItemResultDto> Items);

public sealed record ResponseCrmDeliveryDto(
    Guid Id,
    Guid OfficeId,
    string OfficeName,
    string Outcome,
    Guid? CardId,
    string? ErrorMessage,
    string Source,
    DateTime CreatedAtUtc);
