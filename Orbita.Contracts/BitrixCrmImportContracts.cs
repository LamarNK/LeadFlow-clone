namespace Orbita.Contracts;

public static class BitrixCrmImportStages
{
    public const string MissedCall = "НДЗ";
    public const string Questionnaire = "Анкета";
    public const string Negotiations = "Переговоры";
    public const string LongTermNegotiations = "Переговоры долгосрок";

    public static readonly IReadOnlyList<string> Default =
        [MissedCall, Questionnaire, Negotiations];
}

public static class BitrixCrmImportActions
{
    public const string Create = "create";
    public const string UpdateExisting = "update_existing";
    public const string AlreadyImported = "already_imported";
    public const string MissingPhone = "missing_phone";
}

public sealed record BitrixCrmImportPreviewRequest(
    int CategoryId = 0,
    IReadOnlyList<string>? StageNames = null);

public sealed record BitrixCrmImportExecuteRequest(
    int CategoryId = 0,
    IReadOnlyList<string>? StageNames = null,
    IReadOnlyList<long>? DealIds = null);

public sealed record BitrixCrmImportStageSummaryDto(
    string StageName,
    string BitrixStageId,
    int DealCount);

public sealed record BitrixCrmImportManagerMatchDto(
    long BitrixUserId,
    string BitrixName,
    string? OrbitaUserId,
    string? OrbitaName,
    bool IsMatched);

public sealed record BitrixCrmImportDealPreviewDto(
    long DealId,
    string CandidateName,
    string Phone,
    string City,
    string Vacancy,
    string StageName,
    long? BitrixResponsibleId,
    string BitrixResponsibleName,
    string? OrbitaResponsibleUserId,
    string? OrbitaResponsibleName,
    int CommentCount,
    int ActivityCount,
    string Action,
    Guid? ExistingCardId,
    string? Warning);

public sealed record BitrixCrmImportPreviewDto(
    Guid BitrixInstanceId,
    Guid OfficeId,
    string PortalHost,
    int CategoryId,
    IReadOnlyList<BitrixCrmImportStageSummaryDto> Stages,
    IReadOnlyList<BitrixCrmImportManagerMatchDto> Managers,
    IReadOnlyList<BitrixCrmImportDealPreviewDto> Deals,
    int CreateCount,
    int UpdateCount,
    int AlreadyImportedCount,
    int SkippedCount);

public sealed record BitrixCrmImportItemResultDto(
    long DealId,
    bool Success,
    string Action,
    Guid? CardId,
    string? Error);

public sealed record BitrixCrmImportResultDto(
    int Requested,
    int Created,
    int Updated,
    int AlreadyImported,
    int Skipped,
    IReadOnlyList<BitrixCrmImportItemResultDto> Items);
