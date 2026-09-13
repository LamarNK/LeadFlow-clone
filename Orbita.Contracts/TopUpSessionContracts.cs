namespace Orbita.Contracts;

/// <summary>
/// Статусы сессии ручного пополнения баланса аккаунта воркера.
/// Активные: запрошена оператором, воркер начал, QR готов.
/// Терминальные: истекла, ошибка, отменена, оплачена оператором.
/// Банковский перевод Орбита не видит — «оплачено» ставит только оператор.
/// </summary>
public static class TopUpSessionStatuses
{
    public const string Requested = "requested";
    public const string Started = "started";
    /// <summary>
    /// Воркер атомарно заявил право на клик по оплате (линеаризационный барьер перед
    /// диспетчеризацией клика). Активное состояние: после него допустимы qr_ready/failed/expired.
    /// </summary>
    public const string PaymentClaimed = "payment_claimed";
    public const string QrReady = "qr_ready";
    public const string Expired = "expired";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    /// <summary>Оператор подтвердил, что оплатил QR. Снимает паузу мониторинга.</summary>
    public const string Paid = "paid";
    public const string AwaitingBalance = "awaiting_balance";
    public const string Completed = "completed";
    public const string VerificationRequired = "verification_required";

    public static bool IsActive(string? status) =>
        status is Requested or Started or PaymentClaimed or QrReady;

    /// <summary>
    /// Операция ещё не закрыта: строка не должна висеть на «Требуют пополнения»
    /// и воркер не должен подсвечиваться на главной из‑за неё.
    /// </summary>
    public static bool IsOpenOnLowBalanceTab(string? status) =>
        IsActive(status) || status is AwaitingBalance or VerificationRequired;

    /// <summary>
    /// Разрешённые переходы статусов (только вперёд). Отмена и «оплачено» выставляет
    /// панель из активных состояний; worker может завершить сессию статусами failed/expired.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedTransitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Requested] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Started, Failed, Expired },
            [Started] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PaymentClaimed, QrReady, Failed, Expired },
            [PaymentClaimed] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { QrReady, Failed, Expired },
            [QrReady] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { Paid, AwaitingBalance, VerificationRequired, Completed, Failed, Expired },
            [AwaitingBalance] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Completed, Failed, Expired },
            [VerificationRequired] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Completed, Failed },
        };

    /// <summary>
    /// Проверяет, допустим ли переход <paramref name="from"/> → <paramref name="to"/>.
    /// Терминальные статусы (expired/failed/cancelled/paid) не имеют исходящих переходов.
    /// </summary>
    public static bool CanTransition(string? from, string? to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
        {
            return false;
        }

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return AllowedTransitions.TryGetValue(from, out var targets)
               && targets.Contains(to);
    }
}

/// <summary>
/// Правила расчёта целевого баланса и суммы пополнения по количеству откликов
/// за текущий день. Чистые функции — покрываются юнит-тестами без БД.
/// </summary>
public static class TopUpSessionRules
{
    /// <summary>Пополнение доступно при балансе меньше или равном порогу.</summary>
    public const decimal LowBalanceThresholdRub = 250m;

    public const decimal TierZeroToThreeAmountRub = 300m;
    public const decimal TierFourToFiveAmountRub = 550m;
    public const decimal TierSixToNineAmountRub = 750m;
    public const decimal TierTenPlusAmountRub = 1500m;

    public const int TierZeroToThreeMaxResponses = 3;
    public const int TierFourToFiveMaxResponses = 5;
    public const int TierSixToNineMaxResponses = 9;

    /// <summary>
    /// Срок автоматизации и оплаты QR: от <c>started</c> до готового QR и от
    /// <c>qr_ready</c> до оплаты. Не применяется к сессиям, которые ещё ждут воркера.
    /// </summary>
    public static readonly TimeSpan PauseLeaseTtl = TimeSpan.FromMinutes(10);

    /// <summary>Срок жизни сессии в очереди воркера, пока её не взяли в работу.</summary>
    public static readonly TimeSpan QueueTtl = TimeSpan.FromHours(2);

    /// <summary>
    /// После «Оплачено» ждём обычный снимок баланса со следующего прохода.
    /// Если за это время баланс не вырос на сумму пополнения — сессия ошибочна.
    /// </summary>
    public static readonly TimeSpan BalanceConfirmationTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// После подтверждённого пополнения временно не предлагаем и не разрешаем
    /// новое пополнение, даже если интерфейс Avito ещё показывает низкий баланс.
    /// </summary>
    public static readonly TimeSpan RepeatTopUpCooldown = TimeSpan.FromHours(3);

    /// <summary>Допуск при сверке нового баланса с ожидаемой суммой пополнения.</summary>
    public const decimal BalanceEpsilonRub = 1m;

    public static TimeZoneInfo MoscowTimeZone { get; } = ResolveMoscow();

    public static decimal ResolveFixedTopUpAmount(int dailyResponses) =>
        dailyResponses <= TierZeroToThreeMaxResponses
            ? TierZeroToThreeAmountRub
            : dailyResponses <= TierFourToFiveMaxResponses
                ? TierFourToFiveAmountRub
                : dailyResponses <= TierSixToNineMaxResponses
                    ? TierSixToNineAmountRub
                    : TierTenPlusAmountRub;

    public static decimal ResolveTargetBalance(decimal currentBalance, int dailyResponses) =>
        currentBalance + ResolveFixedTopUpAmount(dailyResponses);

    public static bool IsEligible(decimal currentBalance) =>
        currentBalance <= LowBalanceThresholdRub;

    public static bool IsRepeatTopUpCooldownActive(
        DateTime? completedAtUtc,
        DateTime nowUtc) =>
        completedAtUtc is DateTime completed
        && completed <= nowUtc
        && completed > nowUtc - RepeatTopUpCooldown;

    public static decimal ResolveRequestedAmount(decimal currentBalance, int dailyResponses) =>
        ResolveFixedTopUpAmount(dailyResponses);

    /// <summary>
    /// Снимок подтверждает пополнение, если баланс вырос и достиг ожидаемой суммы
    /// (<c>min(current + requested, target)</c>) с допуском <see cref="BalanceEpsilonRub"/>.
    /// Любой рост на 1 ₽ успехом не считается.
    /// </summary>
    public static bool IsExpectedBalanceIncrease(
        decimal currentBalance,
        decimal requestedAmount,
        decimal targetBalance,
        decimal actualBalance)
    {
        if (actualBalance <= currentBalance)
        {
            return false;
        }

        var expected = requestedAmount > 0m
            ? Math.Min(currentBalance + requestedAmount, targetBalance)
            : targetBalance;
        return actualBalance + BalanceEpsilonRub >= expected;
    }

    /// <summary>
    /// Границы текущего московского календарного дня в UTC (включительно/исключительно).
    /// Используется для подсчёта откликов за день по <c>CollectedAt</c>.
    /// </summary>
    public static (DateTime UtcStartInclusive, DateTime UtcEndExclusive) GetMoscowDayRange(DateTime nowUtc)
    {
        var zone = MoscowTimeZone;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(AssumeUtc(nowUtc), zone);
        var today = DateOnly.FromDateTime(localNow);
        var start = TimeZoneInfo.ConvertTimeToUtc(
            today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone);
        var end = TimeZoneInfo.ConvertTimeToUtc(
            today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone);
        return (DateTime.SpecifyKind(start, DateTimeKind.Utc), DateTime.SpecifyKind(end, DateTimeKind.Utc));
    }

    private static DateTime AssumeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>Санитизация текста прогресса для UI. Пустая строка становится null.</summary>
    public static string? SanitizeProgressMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var cleaned = message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        const int maxLength = 200;
        return cleaned.Length > maxLength
            ? cleaned[..maxLength] + "…"
            : cleaned;
    }

    private static TimeZoneInfo ResolveMoscow()
    {
        foreach (var id in new[] { "Europe/Moscow", "Russian Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("MSK", TimeSpan.FromHours(3), "MSK", "MSK");
    }
}

/// <summary>
/// Валидация QR-данных сессии пополнения. Принимает только корректный base64 PNG-изображения
/// (разумного размера) либо HTTPS-URL на домене Avito. Произвольные URL-схемы отклоняются.
/// </summary>
public static class TopUpSessionQrValidator
{
    /// <summary>Максимальная длина base64-строки (символов) — ~1.5 МБ декодированных данных.</summary>
    public const int MaxBase64Length = 2_000_000;

    /// <summary>Максимальный размер декодированного изображения в байтах.</summary>
    public const int MaxDecodedBytes = 1_500_000;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Проверяет пару QR-данных. Возвращает <c>true</c>, если хотя бы один источник валиден,
    /// иначе <c>false</c> с описанием ошибки. Пустые значения игнорируются (не считаются ошибкой),
    /// но если оба пустые — ошибка.
    /// </summary>
    public static (bool Valid, string? Error) Validate(string? qrImageBase64, string? qrImageUrl)
    {
        var hasBase64 = !string.IsNullOrWhiteSpace(qrImageBase64);
        var hasUrl = !string.IsNullOrWhiteSpace(qrImageUrl);

        if (!hasBase64 && !hasUrl)
        {
            return (false, "QR-данные не переданы.");
        }

        if (hasBase64)
        {
            var base64Error = ValidateBase64(qrImageBase64!);
            if (base64Error is not null)
            {
                return (false, base64Error);
            }

            // Валидный base64 PNG — достаточно, URL не требуется.
            return (true, null);
        }

        // Только URL: разрешаем исключительно HTTPS Avito.
        var urlError = ValidateUrl(qrImageUrl!);
        return urlError is null ? (true, null) : (false, urlError);
    }

    private static string? ValidateBase64(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "QR-изображение пустое.";
        }

        if (trimmed.Length > MaxBase64Length)
        {
            return "QR-изображение слишком большое.";
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            return "QR-изображение не является корректным base64.";
        }

        if (decoded.Length > MaxDecodedBytes)
        {
            return "QR-изображение слишком большое.";
        }

        if (!StartsWithPngSignature(decoded))
        {
            return "QR-изображение не является PNG.";
        }

        return null;
    }

    private static string? ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return "QR-URL некорректен.";
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "QR-URL должен использовать HTTPS.";
        }

        var host = uri.Host;
        if (!IsAvitoHost(host))
        {
            return "QR-URL должен указывать на домен Avito.";
        }

        return null;
    }

    private static bool IsAvitoHost(string host) =>
        string.Equals(host, "avito.ru", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".avito.ru", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "avito.st", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".avito.st", StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithPngSignature(byte[] bytes)
    {
        if (bytes.Length < PngSignature.Length)
        {
            return false;
        }

        for (var i = 0; i < PngSignature.Length; i++)
        {
            if (bytes[i] != PngSignature[i])
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record TopUpSessionDto(
    Guid Id,
    Guid WorkerId,
    string WorkerName,
    Guid AccountId,
    string AccountName,
    Guid OfficeId,
    string OperatorUserId,
    string OperatorDisplayName,
    string Status,
    decimal CurrentBalance,
    decimal TargetBalance,
    decimal RequestedAmount,
    int DailyResponseCount,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? PaymentClaimedAtUtc,
    DateTime? QrReadyAtUtc,
    string? QrImageBase64,
    string? QrImageUrl,
    string? FailureMessage,
    string SubProfileId = "",
    string SubProfileName = "",
    string? ProgressMessage = null,
    decimal? BalanceAfter = null,
    DateTime? BalanceConfirmedAtUtc = null,
    DateTime? AwaitingBalanceAtUtc = null,
    DateTime? HistoryConfirmedAtUtc = null,
    DateTime? HistoryOperationAtUtc = null);

/// <summary>
/// Pending-снимок сессии для воркера (через worker config / push). Воркер уже знает
/// профиль аккаунта из своего конфига, поэтому здесь только параметры пополнения.
/// </summary>
public sealed record WorkerPendingTopUpSessionDto(
    Guid SessionId,
    Guid WorkerId,
    Guid AccountId,
    string AccountName,
    decimal TargetBalance,
    decimal RequestedAmount,
    decimal CurrentBalance,
    int DailyResponseCount,
    string SubProfileId = "",
    string SubProfileName = "");

/// <summary>
/// Сессия с готовым QR, для которой воркер должен проверить историю кошелька
/// на следующем обычном проходе субпрофиля.
/// </summary>
public sealed record WorkerPendingTopUpHistoryCheckDto(
    Guid SessionId,
    Guid AccountId,
    string SubProfileId,
    string SubProfileName,
    decimal RequestedAmount);

public sealed record TopUpHistoryOperationDto(
    decimal Amount,
    DateTime OccurredAtUtc,
    string Description = "Внесение аванса");

public sealed record ConfirmTopUpHistoryRequest(
    Guid AccountId,
    string SubProfileId,
    DateTime CapturedAtUtc,
    IReadOnlyList<TopUpHistoryOperationDto> Operations,
    decimal? AdvanceBalance = null);

public sealed record ConfirmTopUpHistoryResult(
    int ConfirmedCount,
    string? Error = null);

public sealed record UpdateTopUpSessionStatusRequest(
    Guid SessionId,
    string Status,
    string? QrImageBase64 = null,
    string? QrImageUrl = null,
    string? FailureMessage = null,
    string? ProgressMessage = null);

/// <summary>
/// Запрос воркера на атомарное заявление права на клик по оплате (линеаризационный барьер).
/// </summary>
public sealed record ClaimTopUpPaymentRequest(Guid SessionId);

/// <summary>
/// Результат заявления права на оплату. <see cref="Claimed"/> = true — воркер может кликать;
/// иначе <see cref="Error"/> описывает причину (сессия отменена/истекла/уже завершена).
/// </summary>
public sealed record ClaimTopUpPaymentResult(bool Claimed, string? Error = null);

/// <summary>
/// Классификация результата опроса состояния сессии пополнения воркером.
/// </summary>
public enum TopUpSessionPollStatus
{
    /// <summary>Сессия существует и активна.</summary>
    Active,

    /// <summary>Сессия существует, но в терминальном состоянии (cancelled/expired/failed/paid).</summary>
    Terminal,

    /// <summary>Сессия не найдена (404) — окончательное отсутствие.</summary>
    Missing,

    /// <summary>Транзиентная ошибка (429/5xx/сетевой сбой) — можно повторить.</summary>
    Transient,

    /// <summary>Постоянная ошибка (401/403/неожиданный статус) — повтор не поможет.</summary>
    Permanent
}

/// <summary>
/// Классификация HTTP-статусов опроса сессии пополнения.
/// </summary>
public static class TopUpSessionPollStatusClassifier
{
    /// <summary>
    /// Сопоставляет HTTP-статус с классификацией опроса. 404 — окончательное отсутствие;
    /// 429/5xx — транзиентные; 401/403 и прочие неожиданные — постоянные.
    /// </summary>
    public static TopUpSessionPollStatus FromHttpStatusCode(int statusCode) =>
        statusCode switch
        {
            404 => TopUpSessionPollStatus.Missing,
            408 => TopUpSessionPollStatus.Transient,
            429 => TopUpSessionPollStatus.Transient,
            >= 500 => TopUpSessionPollStatus.Transient,
            401 or 403 => TopUpSessionPollStatus.Permanent,
            _ => TopUpSessionPollStatus.Permanent
        };
}

/// <summary>
/// Типизированный результат опроса состояния сессии пополнения. Позволяет наблюдателю отмены
/// отличать окончательное отсутствие/терминальное состояние от транзиентных ошибок.
/// </summary>
public sealed record TopUpSessionPollResult(
    TopUpSessionPollStatus Status,
    TopUpSessionDto? Session = null);

public sealed record TopUpSessionConflictDto(
    string Message,
    Guid? ActiveSessionId = null,
    string? ActiveAccountName = null);
