namespace Orbita.Contracts;

public static class CaptchaSessionStatuses
{
    public const string Pending = "pending";
    public const string Opening = "opening";
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";

    /// <summary>Активные статусы в форме, транслируемой в SQL (для IQueryable.Where).</summary>
    public static readonly string[] ActiveStatuses = [Pending, Opening, Active];

    public static bool IsActive(string? status) =>
        status is Pending or Opening or Active;
}

public sealed record CreateCaptchaSessionRequest(
    Guid AccountId,
    Guid WorkerId,
    string PageUrl,
    string CaptchaKind,
    string? SubProfileId = null);

public sealed record CaptchaSessionDto(
    Guid Id,
    Guid AccountId,
    string AccountName,
    Guid WorkerId,
    string WorkerName,
    Guid OfficeId,
    string OperatorUserId,
    string OperatorDisplayName,
    string PageUrl,
    string CaptchaKind,
    string? SubProfileId,
    string Status,
    int ViewportWidth,
    int ViewportHeight,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? CompletedAtUtc,
    string? FailureMessage);

public sealed record WorkerCaptchaLockDto(
    bool IsLocked,
    Guid? SessionId,
    Guid? AccountId,
    string? AccountName,
    string? OperatorUserId,
    string? OperatorDisplayName,
    DateTime? StartedAtUtc);

public sealed record WorkerPendingCaptchaSessionDto(
    Guid SessionId,
    Guid AccountId,
    string AdsPowerProfileId,
    string? AdsPowerApiBaseUrl,
    string? AdsPowerApiKey,
    string PageUrl,
    string CaptchaKind,
    string? SubProfileId,
    int ViewportWidth,
    int ViewportHeight);

public sealed record UpdateCaptchaSessionStatusRequest(
    Guid SessionId,
    string Status,
    bool? CaptchaCleared = null,
    string? FailureMessage = null);

public sealed record CaptchaSnapshotMessage(
    Guid SessionId,
    string MhtmlGzipBase64,
    int ViewportWidth,
    int ViewportHeight,
    long TimestampMs,
    string ContentType = "image/jpeg");

public sealed record CaptchaFrameMessage(
    Guid SessionId,
    string ImageBase64,
    int ViewportWidth,
    int ViewportHeight,
    long TimestampMs,
    string ContentType = "image/jpeg");

public sealed record CaptchaInputMessage(
    Guid SessionId,
    string EventType,
    double X,
    double Y,
    long TimestampMs,
    double PanelWidth,
    double PanelHeight,
    int Button = 0,
    int Buttons = 0,
    string? Key = null,
    string? Code = null,
    bool AltKey = false,
    bool CtrlKey = false,
    bool ShiftKey = false,
    bool MetaKey = false,
    bool Repeat = false);

public sealed record CaptchaStateChangedMessage(
    Guid SessionId,
    string Status,
    string? Message = null);

public sealed record WorkerCaptchaLockChangedMessage(
    Guid WorkerId,
    Guid OfficeId,
    bool IsLocked,
    WorkerCaptchaLockDto? Lock);

public static class CaptchaViewportDefaults
{
    public const int Width = 1280;
    public const int Height = 800;
}

public sealed record CaptchaSessionConflictDto(
    string Message,
    Guid? ActiveSessionId = null,
    string? ActiveOperatorDisplayName = null,
    string? ActiveAccountName = null);
