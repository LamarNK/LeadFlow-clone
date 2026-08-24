using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LeadFlow.Core.Logging.Audit;

/// <summary>
/// Перечисление уровней логирования.
/// </summary>
public enum DeskLinkAuditLogLevel
{
    Info,
    Debug,
    Warning,
    Error
}

/// <summary>
/// Класс для представления записи лога.
/// </summary>
public class LogFileEntry
{
    /// <summary>
    /// Временная метка лога.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Уровень лога.
    /// </summary>
    public DeskLinkAuditLogLevel Level { get; set; }

    /// <summary>
    /// Сообщение лога.
    /// </summary>
    public string Message { get; set; }

    /// <summary>
    /// Префикс лога в формате [ClassName.MethodName] (извлекается автоматически).
    /// </summary>
    public string Prefix { get; set; }

    /// <summary>
    /// Указывает, была ли запись подделана.
    /// </summary>
    public bool IsTampered { get; set; }

    /// <summary>
    /// Причина подмены записи, если она была подделана.
    /// </summary>
    public string TamperReason { get; set; }

    /// <summary>  // Новое свойство
    /// Предыдущий хэш записи.
    /// </summary>
    public string PrevHash { get; set; }

    /// <summary>  // Новое свойство
    /// Хэш текущей записи.
    /// </summary>
    public string Hash { get; set; }

    /// <summary>
    /// TraceId (корреляция запросов), если доступно.
    /// </summary>
    public string TraceId { get; set; }

    /// <summary>
    /// Дополнительные свойства записи (структурный контекст) в строковом виде.
    /// </summary>
    public string Properties { get; set; }

    /// <summary>
    /// Имя сервиса, из которого записан лог (Gateway, Weather, Ip и т.д.).
    /// </summary>
    public string Service { get; set; }

}

/// <summary>
/// Статический класс для доступа к глобальному экземпляру логгера.
/// </summary>
public static class GlobalLogger
{
    /// <summary>
    /// Экземпляр логгера.
    /// </summary>
    public static Logger Instance { get; private set; }

    /// <summary>
    /// Опциональный "application logger" (стандартный ILogger из ASP.NET Core).
    /// Используется для обычных app-логов; audit-логи продолжают писаться в бинарные файлы.
    /// </summary>
    public static ILogger? AppLogger { get; private set; }

    /// <summary>
    /// Тестовый sink: вызывается из <see cref="Logger.LogAsync"/> с тем же payload, что уходит в журнал.
    /// Свойства — глубокая immutable копия после sanitization; последний аргумент — уже сериализованный envelope.
    /// Не используется в проде.
    /// </summary>
    internal static Action<DeskLinkAuditLogLevel, string, string?, string?, IReadOnlyDictionary<string, object?>, string?>? TestCapture;

    /// <summary>
    /// Подключает стандартный ILogger (для app-логов), не меняя существующие вызовы GlobalLogger.Instance.LogAsync().
    /// </summary>
    public static void ConfigureAppLogger(ILogger logger)
    {
        AppLogger = logger;
        Instance.SetAppLogger(logger);
    }

    /// <summary>Проверка уровня до дорогого форматирования сообщения (LOG_LEVEL / среда).</summary>
    public static bool IsLevelEnabled(DeskLinkAuditLogLevel level) => LogLevelConfiguration.IsEnabled(level);

    /// <summary>
    /// Инициализирует глобальный экземпляр логгера.
    /// LOG_SERVICE_NAME: подпапка в logs (Gateway, Weather, Currency и т.д.). По умолчанию — logs.
    /// LOG_AGGREGATE_READ: true/1 — читать из всех logs/* (только для Gateway).
    /// </summary>
    static GlobalLogger()
    {
        var serviceName = Environment.GetEnvironmentVariable("LOG_SERVICE_NAME") ?? "";
        var logDir = ResolveLogDirectoryForService(serviceName);
        var aggregate = Environment.GetEnvironmentVariable("LOG_AGGREGATE_READ");
        var aggregateRoot = (aggregate == "true" || aggregate == "1")
            ? ResolveLogsRootDirectory(serviceName)
            : null;
        Instance = new Logger(logDir, aggregateRoot);
    }

    public static string ResolveLogsRootDirectory(string serviceName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Data", "logs");
    }

    public static string ResolveLogDirectoryForService(string serviceName)
    {
        var root = ResolveLogsRootDirectory(serviceName);
        if (string.IsNullOrWhiteSpace(serviceName))
            return root;
        return Path.Combine(root, serviceName.Trim());
    }

    /// <summary>
    /// Удаляет устаревшие файлы логов во всех подпапках сервисов (Orbita.Api, Orbita.Web и т.д.).
    /// </summary>
    public static int PruneLogFilesInRoot(string rootPath, DateTime cutoffDate, DateTime minSyncedUtc)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return 0;
        }

        var total = 0;
        foreach (var serviceDir in Directory.GetDirectories(rootPath))
        {
            var logger = new Logger(serviceDir);
            total += logger.PruneLogFilesBeforeAsync(cutoffDate, minSyncedUtc);
        }

        return total;
    }

}

/// <summary>
/// Класс для логирования сообщений с защитой от подмены через цепочку хэшей.
/// </summary>
public class Logger
{
    private readonly string logDirectory;
    private readonly string? aggregateReadRoot;
    private const int MaxFileSizeInBytes = 5 * 1024 * 1024; // 5 MB
    private const int MaxDuplicateErrors = 3; // Жёсткий лимит на дубликаты
    private const int DuplicateWindowSeconds = 60; // Временное окно для дубликатов
    private const int MaxTraceIdBytes = 1024;

    // Бинарный формат: Magic number для проверки формата
    private const uint BinaryFormatMagic = 0x4C4F4753; // "LOGS"
    // v4-only: добавлены отдельные поля TraceId/Properties + footer RecordLength
    // Prefix НЕ дублируется в Message и участвует в вычислении hash
    private const byte BinaryFormatVersion = 4;
    private static readonly byte[] BinaryFormatMagicBytes = BitConverter.GetBytes(BinaryFormatMagic);

    // Индексный файл: для быстрого поиска по TraceId, Level, дате
    private const uint IndexFormatMagic = 0x4C494458; // "LIDX"
    private const byte IndexFormatVersion = 1;

    private readonly Dictionary<string, ErrorCacheEntry> errorCache = new();
    private readonly ConcurrentDictionary<string, List<string>> fileCache = new();
    private readonly SemaphoreSlim writeSemaphore = new(1, 1);

    // Оптимизация hot-path: кэш последнего хэша для текущего файла (читает диск только при первом обращении)
    private readonly ConcurrentDictionary<string, string> lastHashByFile = new();

    // Семафоры для синхронизации записи в индексные файлы
    private readonly ConcurrentDictionary<string, SemaphoreSlim> indexSemaphores = new();

    // Оптимизация hot-path: кэш префикса [Class.Method] на (filePath, memberName)
    private readonly ConcurrentDictionary<string, string> prefixCache = new();
    // Фолбек для single-file: кэш класса, полученного через StackTrace (по имени метода)
    private readonly ConcurrentDictionary<string, string> stackClassByMemberName = new();

    // Наблюдаемость потерь low-priority логов при перегрузке канала
    private long droppedLowPriorityWriteRequests = 0;
    private DateTime lastDroppedReportUtc = DateTime.MinValue;

    // Троттлинг предупреждения о missing errorKey
    private DateTime lastMissingErrorKeyReportUtc = DateTime.MinValue;

    private static readonly Regex GuidRegex = new(@"\b[0-9a-fA-F]{8}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
    private static readonly Regex IpV4Regex = new(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled);
    private static readonly Regex HexRegex = new(@"\b0x[0-9a-fA-F]+\b", RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"\b\d+\b", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions StructuredLogJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly bool JsonlFileEnabled =
        string.Equals(Environment.GetEnvironmentVariable("LOG_JSONL"), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("LOG_JSONL"), "true", StringComparison.OrdinalIgnoreCase);

    // Минимальная наблюдаемость внутренних ошибок логгера (с троттлингом)
    private DateTime lastInternalErrorReportedUtc = DateTime.MinValue;

    // Ограничение роста errorCache
    private DateTime lastErrorCacheCleanupUtc = DateTime.MinValue;
    private const int ErrorCacheTtlSeconds = DuplicateWindowSeconds * 5;
    private const int ErrorCacheCleanupMinIntervalSeconds = 60;

    // Убираем I/O из hot-path: запись идёт в фоновом воркере
    private const int WriteChannelCapacity = 2048;
    private readonly Channel<LogWriteRequest> writeChannel;
    private readonly CancellationTokenSource writeCts = new();
    private readonly Task writerTask;

    private readonly record struct LogWriteRequest(
        DateTime Timestamp,
        DeskLinkAuditLogLevel Level,
        string Prefix,
        string Message,
        string? ErrorKey,
        string? TraceId,
        string? PropertiesJson
    );

    private readonly record struct ErrorCacheEntry(
        int Count,
        DateTime LastSeen,
        int SuppressedSinceSummary,
        DateTime LastSummaryLogged
    );

    /// <summary>
    /// Запись индекса для быстрого поиска.
    /// Хранит позицию записи в логе и ключевые поля для фильтрации.
    /// </summary>
    private readonly record struct IndexEntry(
        long FileOffset,           // Позиция записи в файле лога
        DateTime Timestamp,        // Дата/время для фильтрации по диапазону
        DeskLinkAuditLogLevel Level,            // Уровень для фильтрации
        string? TraceId            // TraceId для поиска связанных записей
    );

    /// <summary>
    /// Делегат для обработки ошибок логирования.
    /// </summary>
    public delegate void LogErrorHandler(string message);

    /// <summary>
    /// Событие, вызываемое при логировании ошибки.
    /// </summary>
    public event LogErrorHandler OnErrorLogged;

    private ILogger? appLogger;

    /// <summary>
    /// Подключает стандартный ILogger (для app-логов).
    /// </summary>
    public void SetAppLogger(ILogger logger) => appLogger = logger;

    /// <summary>
    /// Конструктор логгера.
    /// </summary>
    /// <param name="logDirectory">Директория для хранения логов (запись).</param>
    /// <param name="aggregateReadRoot">Корень для чтения логов всех сервисов (logs/ServiceName). Если задан — при чтении сканируются все подпапки.</param>
    public Logger(string logDirectory, string? aggregateReadRoot = null)
    {
        this.logDirectory = Path.IsPathRooted(logDirectory)
            ? logDirectory
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, logDirectory));
        this.aggregateReadRoot = string.IsNullOrWhiteSpace(aggregateReadRoot)
            ? null
            : (Path.IsPathRooted(aggregateReadRoot)
                ? aggregateReadRoot
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, aggregateReadRoot)));
        if (!Directory.Exists(this.logDirectory))
        {
            Directory.CreateDirectory(this.logDirectory);
        }

        writeChannel = Channel.CreateBounded<LogWriteRequest>(new BoundedChannelOptions(WriteChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        writerTask = Task.Run(() => WriterLoopAsync(writeCts.Token));

        // Пытаемся корректно завершить writer при завершении процесса
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                writeCts.Cancel();
                writeChannel.Writer.TryComplete();
                writerTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore
            }
        };
    }

    /// <summary>
    /// Асинхронно записывает сообщение в лог с автоматическим определением класса и метода.
    /// Префикс [ClassName.MethodName] добавляется автоматически.
    /// </summary>
    /// <param name="message">Сообщение для логирования.</param>
    /// <param name="level">Уровень лога (по умолчанию Info).</param>
    /// <param name="memberName">Имя метода (автоматически определяется компилятором).</param>
    /// <param name="filePath">Путь к файлу (автоматически определяется компилятором).</param>
    public Task LogAsync(
        Func<string> messageFactory,
        DeskLinkAuditLogLevel level = DeskLinkAuditLogLevel.Info,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        string? errorKey = null,
        Dictionary<string, object?>? properties = null)
    {
        if (!LogLevelConfiguration.IsEnabled(level))
            return Task.CompletedTask;
        string message;
        try
        {
            message = messageFactory();
        }
        catch
        {
            return Task.CompletedTask;
        }

        return LogAsync(message, level, memberName, filePath, errorKey, properties);
    }

    /// <param name="filePath">Путь к файлу (автоматически определяется компилятором).</param>
    public async Task LogAsync(
        string message,
        DeskLinkAuditLogLevel level = DeskLinkAuditLogLevel.Info,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        string? errorKey = null,
        Dictionary<string, object?>? properties = null)
    {
        if (!LogLevelConfiguration.IsEnabled(level))
            return;

        // Оптимизация: кешируем вычисление prefix (особенно важно в single-file, где CallerFilePath пуст и нужен StackTrace)
        string prefixKey = string.Concat(filePath ?? string.Empty, "|", memberName ?? string.Empty);
        if (!prefixCache.TryGetValue(prefixKey, out var prefix))
        {
            // Извлекаем имя класса из пути к файлу (только имя файла, без полного пути)
            string className = GetClassNameFromFilePath(filePath);
            if (string.IsNullOrEmpty(className))
            {
                // Фолбек через стек вызовов, если CallerFilePath пуст или недоступен
                string mn = memberName ?? string.Empty;
                if (!string.IsNullOrEmpty(mn))
                {
                    className = stackClassByMemberName.GetOrAdd(mn, _ => GetClassNameFromStack());
                }
                else
                {
                    className = GetClassNameFromStack();
                }
            }

            // Формируем префикс в формате [ClassName.MethodName]
            // Используем только имя класса, без полного пути
            prefix = "";
            if (!string.IsNullOrEmpty(className) && !string.IsNullOrEmpty(memberName))
            {
                prefix = $"[{className}.{memberName}]";
            }
            else if (!string.IsNullOrEmpty(className))
            {
                prefix = $"[{className}]";
            }
            else if (!string.IsNullOrEmpty(memberName))
            {
                prefix = $"[{memberName}]";
            }

            prefixCache[prefixKey] = prefix;
        }

        await WriteLogAsync(message, level, prefix, errorKey, properties, memberName);
    }

    private async Task WriteLogAsync(
        string message,
        DeskLinkAuditLogLevel level,
        string prefix,
        string? errorKey,
        Dictionary<string, object?>? properties,
        string? memberName)
    {
        var activity = Activity.Current;
        string? correlationId = TruncateUtf8(CorrelationContext.Current, MaxTraceIdBytes);
        string? otelTraceId = TruncateUtf8(activity?.TraceId.ToString(), MaxTraceIdBytes);
        string? spanId = TruncateUtf8(activity?.SpanId.ToString(), MaxTraceIdBytes);
        string? traceForIndex = !string.IsNullOrEmpty(correlationId) ? correlationId : otelTraceId;

        var context = properties != null
            ? LogSanitizer.SanitizeDictionary(properties)
            : new Dictionary<string, object?>();

        string? propsJson = BuildStructuredPropertiesJson(
            DateTime.UtcNow,
            level,
            prefix,
            message ?? string.Empty,
            errorKey,
            correlationId,
            otelTraceId,
            spanId,
            context);
        NotifyTestCapture(level, message, memberName, errorKey, context, propsJson);

        // App-лог: стандартный ILogger (если подключён)
        ForwardToAppLogger(level, prefix, message, errorKey, traceForIndex, propsJson);

        // Audit-лог: пишем в бинарный файл
        await LogInternalAsync(message, level, prefix, errorKey, traceForIndex, propsJson);
    }

    private static void NotifyTestCapture(
        DeskLinkAuditLogLevel level,
        string message,
        string? memberName,
        string? errorKey,
        IReadOnlyDictionary<string, object?> sanitizedProperties,
        string? serializedPayload)
    {
        try
        {
            GlobalLogger.TestCapture?.Invoke(
                level,
                message,
                memberName,
                errorKey,
                FreezeProperties(sanitizedProperties),
                serializedPayload);
        }
        catch
        {
            // тестовый sink не должен ломать логирование
        }
    }

    private static IReadOnlyDictionary<string, object?> FreezeProperties(
        IReadOnlyDictionary<string, object?> source)
    {
        var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in source)
        {
            copy[kv.Key] = FreezeValue(kv.Value);
        }

        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(copy);
    }

    private static object? FreezeValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string:
            case ValueType:
                return value;
            case IReadOnlyDictionary<string, object?> nested:
                return FreezeProperties(nested);
            case Array array:
            {
                var frozen = new object?[array.Length];
                for (var i = 0; i < array.Length; i++)
                {
                    frozen[i] = FreezeValue(array.GetValue(i));
                }

                return Array.AsReadOnly(frozen);
            }
            case IList list:
            {
                var frozen = new object?[list.Count];
                for (var i = 0; i < list.Count; i++)
                {
                    frozen[i] = FreezeValue(list[i]);
                }

                return Array.AsReadOnly(frozen);
            }
            case IEnumerable enumerable:
            {
                var items = new List<object?>();
                foreach (var item in enumerable)
                {
                    items.Add(FreezeValue(item));
                }

                return Array.AsReadOnly(items.ToArray());
            }
            default:
                return value;
        }
    }

    private static string MapLevelString(DeskLinkAuditLogLevel level) => level switch
    {
        DeskLinkAuditLogLevel.Debug => "debug",
        DeskLinkAuditLogLevel.Info => "info",
        DeskLinkAuditLogLevel.Warning => "warn",
        DeskLinkAuditLogLevel.Error => "error",
        _ => "info"
    };

    private static string? BuildStructuredPropertiesJson(
        DateTime timestampUtc,
        DeskLinkAuditLogLevel level,
        string prefix,
        string message,
        string? errorKey,
        string? correlationId,
        string? otelTraceId,
        string? spanId,
        IReadOnlyDictionary<string, object?>? userProps)
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                      ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                      ?? "Production";
            var serviceName = Environment.GetEnvironmentVariable("LOG_SERVICE_NAME") ?? "";

            var context = userProps ?? new Dictionary<string, object?>();

            var envelope = new Dictionary<string, object?>
            {
                ["timestamp"] = timestampUtc.ToString("o", CultureInfo.InvariantCulture),
                ["level"] = MapLevelString(level),
                ["service_name"] = serviceName,
                ["environment"] = env,
                ["correlation_id"] = correlationId ?? "",
                ["trace_id"] = otelTraceId ?? "",
                ["span_id"] = spanId ?? "",
                ["error_key"] = errorKey ?? "",
                ["caller"] = prefix ?? "",
                ["message"] = TruncateUtf8(message, 1024 * 64) ?? "",
                ["context"] = context
            };

            return JsonSerializer.Serialize(envelope, StructuredLogJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Внутренний метод для записи сообщения в лог (без автоматического префикса).
    /// </summary>
    /// <param name="message">Сообщение для логирования (уже с префиксом, если нужно).</param>
    /// <param name="level">Уровень лога.</param>
    /// <param name="prefix">Префикс в формате [ClassName.MethodName] (опционально).</param>
    private async Task LogInternalAsync(string message, DeskLinkAuditLogLevel level, string prefix = "", string? errorKey = null, string? traceId = null, string? propertiesJson = null)
    {
        // Hot-path: только enqueue, без файлового I/O
        var req = new LogWriteRequest(DateTime.UtcNow, level, prefix ?? string.Empty, message ?? string.Empty, errorKey,
            TruncateUtf8(traceId, MaxTraceIdBytes),
            propertiesJson);

        // Важно: низкие уровни не должны блокировать бизнес-поток при перегрузке
        if (level == DeskLinkAuditLogLevel.Debug || level == DeskLinkAuditLogLevel.Info)
        {
            if (!writeChannel.Writer.TryWrite(req))
            {
                Interlocked.Increment(ref droppedLowPriorityWriteRequests);
                ReportDroppedWritesIfNeeded();
                return;
            }

            return;
        }

        await writeChannel.Writer.WriteAsync(req);
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var req in writeChannel.Reader.ReadAllAsync(ct))
            {
                await WriteToDiskAsync(req);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            ReportInternalLoggerError("WriterLoopAsync", ex);
        }
    }

    private async Task WriteToDiskAsync(LogWriteRequest req)
    {
        string? errorEventMessage = null;

        await writeSemaphore.WaitAsync();
        try
        {
            // В бинарном формате не нужно экранировать переносы строк
            // message остается как есть

            // Периодическая уборка errorCache
            CleanupErrorCacheIfNeeded(req.Timestamp);

            if (req.Level == DeskLinkAuditLogLevel.Error)
            {
                // Для Error желательно всегда передавать стабильный errorKey.
                // Если его нет — используем нормализованный message, чтобы дедуп работал лучше на динамических значениях (id/числа/Guid/IP).
                string dedupeKeyCore;
                if (!string.IsNullOrWhiteSpace(req.ErrorKey))
                {
                    dedupeKeyCore = req.ErrorKey!;
                }
                else
                {
                    dedupeKeyCore = NormalizeForDedupe(req.Message);
                    ReportMissingErrorKeyIfNeeded(req.Prefix, req.Message);
                }

                string dedupeKey = $"{req.Prefix}|{dedupeKeyCore}";
                var (isDuplicate, shouldLogSummary, suppressedForSummary) = TrackDuplicate(dedupeKey, req.Timestamp);
                if (isDuplicate && !shouldLogSummary)
                {
                    return; // подавляем
                }

                if (isDuplicate && shouldLogSummary)
                {
                    await WriteOneEntryAsync(req.Timestamp, req.Level, req.Prefix, req.Message, req.TraceId, req.PropertiesJson,
                        suffix: $" (suppressed {suppressedForSummary} duplicates)");
                    errorEventMessage = $"{req.Message} (suppressed {suppressedForSummary} duplicates)";
                    return;
                }
            }

            await WriteOneEntryAsync(req.Timestamp, req.Level, req.Prefix, req.Message, req.TraceId, req.PropertiesJson);

            if (req.Level == DeskLinkAuditLogLevel.Error)
            {
                errorEventMessage = req.Message;
            }
        }
        finally
        {
            writeSemaphore.Release();
        }

        // Событие вызываем вне критической секции, чтобы подписчики не блокировали запись/дедуп/индексацию
        if (!string.IsNullOrEmpty(errorEventMessage))
        {
            try
            {
                OnErrorLogged?.Invoke(errorEventMessage);
            }
            catch (Exception ex)
            {
                ReportInternalLoggerError("OnErrorLogged", ex);
            }
        }
    }

    private async Task WriteOneEntryAsync(DateTime timestamp, DeskLinkAuditLogLevel level, string prefix, string message, string? traceId, string? propertiesJson, string? suffix = null)
    {
        string currentLogFilePath = GetLogFilePath();
        string baseMessage = suffix == null ? message : (message + suffix);
        string finalMessage = baseMessage;

        string prevHash = await GetPreviousHashAsync(currentLogFilePath);
        string? safeTraceId = TruncateUtf8(traceId, MaxTraceIdBytes);
        string logContent = BuildLogContent(timestamp, level, prefix, finalMessage, safeTraceId, propertiesJson);
        string hash = ComputeSHA256Hash(prevHash + logContent);

        long entryOffset;
        using (var fileStream = new FileStream(currentLogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            entryOffset = fileStream.Position;
            await WriteBinaryLogEntryAsync(fileStream, timestamp, level, prefix, finalMessage, safeTraceId, propertiesJson, prevHash, hash);
        }

        lastHashByFile[currentLogFilePath] = hash;

        // Записываем индексную запись для быстрого поиска
        await AppendIndexEntryAsync(currentLogFilePath, entryOffset, timestamp, level, safeTraceId);

        await RotateLogFileIfNeeded(currentLogFilePath);
        InvalidateCache(timestamp);

        if (JsonlFileEnabled && !string.IsNullOrEmpty(propertiesJson))
            await AppendJsonlLineAsync(timestamp, propertiesJson);
    }

    private async Task AppendJsonlLineAsync(DateTime timestamp, string jsonLine)
    {
        try
        {
            var dir = Path.Combine(logDirectory, "jsonl");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"app-{timestamp:yyyy-MM-dd}.jsonl");
            await File.AppendAllTextAsync(path, jsonLine + Environment.NewLine).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportInternalLoggerError("AppendJsonlLineAsync", ex);
        }
    }

    private void ReportDroppedWritesIfNeeded()
    {
        // Минимальная диагностика, чтобы понимать, что логи дропаются из-за перегрузки.
        var nowUtc = DateTime.UtcNow;
        if ((nowUtc - lastDroppedReportUtc).TotalSeconds < 10)
        {
            return;
        }

        lastDroppedReportUtc = nowUtc;
        try
        {
            long dropped = Interlocked.Read(ref droppedLowPriorityWriteRequests);
            if (dropped > 0)
            {
                WriteBootstrapStderr("warn", $"Dropped low-priority log writes due to backpressure. dropped={dropped}");
            }
        }
        catch
        {
            // ignore
        }
    }

    private void ReportMissingErrorKeyIfNeeded(string prefix, string message)
    {
        var nowUtc = DateTime.UtcNow;
        if ((nowUtc - lastMissingErrorKeyReportUtc).TotalSeconds < 30)
        {
            return;
        }

        lastMissingErrorKeyReportUtc = nowUtc;
        try
        {
            // Пишем минимально и без чувствительных данных: без полного message (там может быть PII)
            WriteBootstrapStderr("warn", $"Error logged without errorKey. prefix='{prefix}'");
        }
        catch
        {
            // ignore
        }
    }

    private static string NormalizeForDedupe(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;

        string s = message;
        s = GuidRegex.Replace(s, "{guid}");
        s = IpV4Regex.Replace(s, "{ip}");
        s = HexRegex.Replace(s, "{hex}");
        s = NumberRegex.Replace(s, "{n}");

        // Ограничим рост ключа дедупликации
        const int maxLen = 512;
        if (s.Length > maxLen)
        {
            s = s.Substring(0, maxLen);
        }

        return s;
    }

    private static string? TruncateUtf8(string? value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (maxBytes <= 0) return string.Empty;

        // Быстрый путь
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;

        // Медленный путь: подбираем максимальный префикс строки, который влезает в maxBytes UTF8
        int bytes = 0;
        var sb = new StringBuilder(capacity: Math.Min(value.Length, 256));
        for (int i = 0; i < value.Length;)
        {
            int chars = (i + 1 < value.Length && char.IsSurrogatePair(value[i], value[i + 1])) ? 2 : 1;
            int b = Encoding.UTF8.GetByteCount(value.AsSpan(i, chars));
            if (bytes + b > maxBytes) break;

            sb.Append(value, i, chars);
            bytes += b;
            i += chars;
        }

        return sb.ToString();
    }

    private static void WriteBootstrapStderr(string level, string message)
    {
        try
        {
            var line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["level"] = level,
                ["component"] = "GlobalLogger",
                ["message"] = message
            }, StructuredLogJsonOptions);
            Console.Error.WriteLine(line);
        }
        catch
        {
            // ignore
        }
    }

    private void ForwardToAppLogger(DeskLinkAuditLogLevel level, string prefix, string message, string? errorKey, string? correlationOrTraceId, string? propertiesJson)
    {
        var logger = appLogger;
        if (logger == null) return;

        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "";
        var service = Environment.GetEnvironmentVariable("LOG_SERVICE_NAME") ?? "";

        var scope = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["service_name"] = service,
            ["environment"] = env,
            ["correlation_id"] = correlationOrTraceId ?? ""
        };
        if (!string.IsNullOrWhiteSpace(errorKey))
            scope["error_key"] = errorKey!;
        if (!string.IsNullOrEmpty(propertiesJson))
            scope["structured_log"] = propertiesJson;

        using (logger.BeginScope(scope))
        {
            string text = $"{prefix} {message}".Trim();
            switch (level)
            {
                case DeskLinkAuditLogLevel.Debug:
                    logger.LogDebug("{Message}", text);
                    break;
                case DeskLinkAuditLogLevel.Warning:
                    logger.LogWarning("{Message}", text);
                    break;
                case DeskLinkAuditLogLevel.Error:
                    logger.LogError("{Message}", text);
                    break;
                default:
                    logger.LogInformation("{Message}", text);
                    break;
            }
        }
    }

    /// <summary>
    /// Формирует строку содержимого записи, участвующую в вычислении хэша (должна совпадать при чтении).
    /// </summary>
    private static string BuildLogContent(DateTime timestamp, DeskLinkAuditLogLevel level, string prefix, string message, string? traceId, string? properties)
    {
        // v4-only: Prefix/TraceId/Properties участвуют в хэше как отдельные поля (не через Message)
        // ВАЖНО: этот формат должен совпадать в записи и в чтении.
        return $"{timestamp:yyyy-MM-dd HH:mm:ss}|{level}|{prefix}|{traceId}|{properties}|{message}";
    }

    /// <summary>
    /// Внутренняя диагностика ошибок логгера (не через сам логгер, чтобы не зациклиться).
    /// </summary>
    private void ReportInternalLoggerError(string operation, Exception ex, string? filePath = null)
    {
        var nowUtc = DateTime.UtcNow;
        if ((nowUtc - lastInternalErrorReportedUtc).TotalSeconds < 5)
        {
            return;
        }

        lastInternalErrorReportedUtc = nowUtc;
        try
        {
            string pathPart = string.IsNullOrEmpty(filePath) ? string.Empty : $" file='{filePath}'";
            WriteBootstrapStderr("error", $"{operation} failed.{pathPart} ex='{ex.GetType().Name}': {ex.Message}");
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Извлекает имя класса из пути к файлу (только имя файла, без полного пути).
    /// </summary>
    /// <param name="filePath">Полный путь к файлу.</param>
    /// <returns>Имя класса (имя файла без расширения) или пустая строка, если не удалось определить.</returns>
    private string GetClassNameFromFilePath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return string.Empty;

        try
        {
            // Сначала получаем только имя файла с расширением
            string fileNameWithExt = Path.GetFileName(filePath);
            
            // Если получили пустую строку, возвращаем пустую строку
            if (string.IsNullOrEmpty(fileNameWithExt))
                return string.Empty;
            
            // Проверяем, что это действительно имя файла, а не путь
            // Path.GetFileName не должен возвращать путь, но проверим на всякий случай
            if (fileNameWithExt.IndexOfAny(new[] { '\\', '/' }) >= 0)
                return string.Empty;
            
            // Теперь убираем расширение
            string fileName = Path.GetFileNameWithoutExtension(fileNameWithExt);
            
            // Финальная проверка
            if (string.IsNullOrEmpty(fileName))
                return string.Empty;

            return fileName;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Фолбек: пытается определить имя класса через стек вызовов (для случаев,
    /// когда CallerFilePath недоступен, например при публикации single-file).
    /// </summary>
    private string GetClassNameFromStack()
    {
        try
        {
            var stack = new StackTrace(1, false); // пропускаем текущий метод
            foreach (var frame in stack.GetFrames() ?? Array.Empty<StackFrame>())
            {
                var method = frame.GetMethod();
                var type = method?.DeclaringType;
                if (type == null || type == typeof(Logger))
                    continue;

                // Пропускаем служебные типы компилятора и BCL
                var ns = type.Namespace ?? string.Empty;
                if (ns.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                    ns.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                    type.Name.Contains("AsyncMethodBuilderCore", StringComparison.Ordinal))
                {
                    continue;
                }

                // Для async state machine возвращаем внешний тип, если он есть
                while (type.Name.StartsWith("<", StringComparison.Ordinal) && type.DeclaringType != null)
                {
                    type = type.DeclaringType;
                }

                if (type == null || type == typeof(Logger))
                    continue;

                return type.Name ?? string.Empty;
            }
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }

    /// <summary>
    /// Получает хэш предыдущей записи из файла.
    /// </summary>
    /// <param name="filePath">Путь к файлу лога.</param>
    /// <returns>Хэш предыдущей записи или "genesis", если файл пуст.</returns>
    private async Task<string> GetPreviousHashAsync(string filePath)
    {
        string prevHash = "genesis";

        if (lastHashByFile.TryGetValue(filePath, out var cachedHash) && !string.IsNullOrEmpty(cachedHash))
        {
            return cachedHash;
        }

        if (!File.Exists(filePath))
        {
            lastHashByFile[filePath] = prevHash;
            return prevHash;
        }

        // Бинарный формат
        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fileStream.Length == 0)
            {
                lastHashByFile[filePath] = prevHash;
                return prevHash;
            }

            // v3: читаем RecordLength (footer) и делаем быстрый Seek на последнюю запись
            if (fileStream.Length < 4)
            {
                lastHashByFile[filePath] = prevHash;
                return prevHash;
            }

            fileStream.Position = fileStream.Length - 4;
            byte[] lenBuffer = new byte[4];
            int lenRead = await fileStream.ReadAsync(lenBuffer, 0, 4);
            if (lenRead != 4)
            {
                lastHashByFile[filePath] = prevHash;
                return prevHash;
            }

            int recordLen = BitConverter.ToInt32(lenBuffer, 0);
            if (recordLen <= 0 || recordLen > fileStream.Length)
            {
                lastHashByFile[filePath] = prevHash;
                return prevHash;
            }

            fileStream.Position = fileStream.Length - recordLen;
            var entry = await ReadBinaryLogEntryAsync(fileStream);
            if (entry != null)
            {
                prevHash = entry.Hash ?? "genesis";
            }
        }
        catch (Exception ex)
        {
            // В случае ошибки возвращаем genesis
            ReportInternalLoggerError("GetPreviousHashAsync", ex, filePath);
        }

        lastHashByFile[filePath] = prevHash;
        return prevHash;
    }

    /// <summary>
    /// Вычисляет SHA256 хэш строки.
    /// </summary>
    /// <param name="input">Входная строка.</param>
    /// <returns>Хэш в шестнадцатеричном формате.</returns>
    private string ComputeSHA256Hash(string input)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = sha256.ComputeHash(bytes);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        }
    }

    /// <summary>
    /// Получает путь к индексному файлу для заданного файла лога.
    /// </summary>
    private string GetIndexFilePath(string logFilePath)
    {
        return Path.ChangeExtension(logFilePath, ".idx");
    }

    /// <summary>
    /// Записывает индексную запись в индексный файл.
    /// Индексный файл: [Magic 4b] [Version 1b] [Count 4b] [Entry1] [Entry2] ...
    /// Entry: [Offset 8b] [Timestamp 8b] [Level 1b] [TraceIdLen 4b] [TraceId bytes]
    /// </summary>
    private async Task AppendIndexEntryAsync(string logFilePath, long fileOffset, DateTime timestamp, DeskLinkAuditLogLevel level, string? traceId)
    {
        string indexPath = GetIndexFilePath(logFilePath);
        
        // Получаем или создаём семафор для этого индексного файла
        var semaphore = indexSemaphores.GetOrAdd(indexPath, _ => new SemaphoreSlim(1, 1));
        
        await semaphore.WaitAsync();
        try
        {
            bool isNewFile = !File.Exists(indexPath);
            
            // Разрешаем параллельное чтение индекса. Запись синхронизирована семафором.
            using var fs = new FileStream(indexPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            
            if (isNewFile || fs.Length == 0)
            {
                // Создаём новый индексный файл с заголовком
                fs.Position = 0;
                await fs.WriteAsync(BitConverter.GetBytes(IndexFormatMagic), 0, 4);
                fs.WriteByte(IndexFormatVersion);
                await fs.WriteAsync(BitConverter.GetBytes(0), 0, 4); // Count = 0 initially
            }
            
            // Читаем текущий count
            fs.Position = 5; // После Magic (4) + Version (1)
            byte[] countBuffer = new byte[4];
            int count = 0;
            if (await fs.ReadAsync(countBuffer, 0, 4) == 4)
            {
                count = BitConverter.ToInt32(countBuffer, 0);
                if (count < 0) count = 0;
            }
            
            // Переходим в конец для записи новой записи
            fs.Position = fs.Length;
            
            // Пишем IndexEntry
            await fs.WriteAsync(BitConverter.GetBytes(fileOffset), 0, 8);
            await fs.WriteAsync(BitConverter.GetBytes(timestamp.Ticks), 0, 8);
            fs.WriteByte((byte)level);
            
            byte[] traceIdBytes = Encoding.UTF8.GetBytes(TruncateUtf8(traceId, MaxTraceIdBytes) ?? string.Empty);
            await fs.WriteAsync(BitConverter.GetBytes(traceIdBytes.Length), 0, 4);
            if (traceIdBytes.Length > 0)
            {
                await fs.WriteAsync(traceIdBytes, 0, traceIdBytes.Length);
            }
            
            // Обновляем count в заголовке
            fs.Position = 5;
            await fs.WriteAsync(BitConverter.GetBytes(count + 1), 0, 4);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Читает все индексные записи из индексного файла.
    /// </summary>
    private async Task<List<IndexEntry>> ReadIndexFileAsync(string logFilePath)
    {
        var result = new List<IndexEntry>();
        string indexPath = GetIndexFilePath(logFilePath);
        
        if (!File.Exists(indexPath))
            return result;
        
        try
        {
            using var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            
            if (fs.Length < 9) // Magic + Version + Count minimum
                return result;
            
            // Проверяем magic и version
            byte[] magicBuffer = new byte[4];
            await fs.ReadAsync(magicBuffer, 0, 4);
            if (BitConverter.ToUInt32(magicBuffer, 0) != IndexFormatMagic)
                return result;
            
            int version = fs.ReadByte();
            if (version != IndexFormatVersion)
                return result;
            
            // Читаем count
            byte[] countBuffer = new byte[4];
            await fs.ReadAsync(countBuffer, 0, 4);
            int count = BitConverter.ToInt32(countBuffer, 0);

            // Count в header может быть некорректным (падение между append и обновлением заголовка).
            // Ограничиваем максимально возможным числом записей по размеру файла (верхняя граница).
            const int headerSize = 9;
            const int minEntrySize = 8 + 8 + 1 + 4; // Offset + Timestamp + Level + TraceIdLen (без данных)
            long maxEntriesBySizeLong = (fs.Length - headerSize) / minEntrySize;
            int maxEntriesBySize = maxEntriesBySizeLong > int.MaxValue ? int.MaxValue : (int)Math.Max(0, maxEntriesBySizeLong);
            if (count < 0 || count > maxEntriesBySize)
            {
                count = maxEntriesBySize;
            }
            
            // Читаем записи
            for (int i = 0; i < count; i++)
            {
                byte[] offsetBuffer = new byte[8];
                byte[] timestampBuffer = new byte[8];
                byte[] traceIdLenBuffer = new byte[4];
                
                if (await fs.ReadAsync(offsetBuffer, 0, 8) != 8) break;
                if (await fs.ReadAsync(timestampBuffer, 0, 8) != 8) break;
                
                int levelByte = fs.ReadByte();
                if (levelByte == -1) break;
                
                if (await fs.ReadAsync(traceIdLenBuffer, 0, 4) != 4) break;
                
                long fileOffset = BitConverter.ToInt64(offsetBuffer, 0);
                DateTime timestamp = new DateTime(BitConverter.ToInt64(timestampBuffer, 0), DateTimeKind.Utc);
                DeskLinkAuditLogLevel level = (DeskLinkAuditLogLevel)levelByte;
                int traceIdLen = BitConverter.ToInt32(traceIdLenBuffer, 0);
                
                string? traceId = null;
                if (traceIdLen > 0)
                {
                    if (traceIdLen > MaxTraceIdBytes) break; // Защита от повреждённых данных
                    byte[] traceIdBuffer = new byte[traceIdLen];
                    if (await fs.ReadAsync(traceIdBuffer, 0, traceIdLen) != traceIdLen) break;
                    traceId = Encoding.UTF8.GetString(traceIdBuffer);
                }
                
                result.Add(new IndexEntry(fileOffset, timestamp, level, traceId));
            }
        }
        catch (Exception ex)
        {
            ReportInternalLoggerError("ReadIndexFileAsync", ex, indexPath);
        }
        
        return result;
    }

    /// <summary>
    /// Перестраивает индексный файл для заданного файла лога (если индекс повреждён или отсутствует).
    /// </summary>
    private async Task RebuildIndexAsync(string logFilePath)
    {
        string indexPath = GetIndexFilePath(logFilePath);
        
        var semaphore = indexSemaphores.GetOrAdd(indexPath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            // Удаляем старый индексный файл
            if (File.Exists(indexPath))
            {
                File.Delete(indexPath);
            }
            
            // Читаем все записи из лога и строим индекс заново (одним проходом)
            using var logFs = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var idxFs = new FileStream(indexPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            
            idxFs.Position = 0;
            await idxFs.WriteAsync(BitConverter.GetBytes(IndexFormatMagic), 0, 4);
            idxFs.WriteByte(IndexFormatVersion);
            await idxFs.WriteAsync(BitConverter.GetBytes(0), 0, 4); // Count placeholder
            
            int count = 0;
            while (logFs.Position < logFs.Length)
            {
                long entryOffset = logFs.Position;
                var entry = await ReadBinaryLogEntryAsync(logFs);
                
                if (entry == null)
                {
                    // Пытаемся ресинхронизироваться
                    if (!TryResyncToNextMagic(logFs))
                        break;
                    continue;
                }
                
                await idxFs.WriteAsync(BitConverter.GetBytes(entryOffset), 0, 8);
                await idxFs.WriteAsync(BitConverter.GetBytes(entry.Timestamp.Ticks), 0, 8);
                idxFs.WriteByte((byte)entry.Level);
                
                byte[] traceIdBytes = Encoding.UTF8.GetBytes(TruncateUtf8(entry.TraceId, MaxTraceIdBytes) ?? string.Empty);
                await idxFs.WriteAsync(BitConverter.GetBytes(traceIdBytes.Length), 0, 4);
                if (traceIdBytes.Length > 0)
                {
                    await idxFs.WriteAsync(traceIdBytes, 0, traceIdBytes.Length);
                }
                
                count++;
            }
            
            // Обновляем count в заголовке
            idxFs.Position = 5;
            await idxFs.WriteAsync(BitConverter.GetBytes(count), 0, 4);
        }
        catch (Exception ex)
        {
            ReportInternalLoggerError("RebuildIndexAsync", ex, logFilePath);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Записывает запись лога в бинарном формате.
    /// </summary>
    /// <param name="stream">Поток для записи.</param>
    /// <param name="timestamp">Временная метка.</param>
    /// <param name="level">Уровень лога.</param>
    /// <param name="prefix">Префикс.</param>
    /// <param name="message">Сообщение.</param>
    /// <param name="prevHash">Предыдущий хэш.</param>
    /// <param name="hash">Текущий хэш.</param>
    private async Task WriteBinaryLogEntryAsync(Stream stream, DateTime timestamp, DeskLinkAuditLogLevel level, string prefix, string message, string? traceId, string? properties, string prevHash, string hash)
    {
        // v4: формируем запись в буфере, чтобы дописать footer RecordLength (для быстрых Seek/count)
        using var ms = new MemoryStream(capacity: 512);

        // Magic number для проверки формата
        ms.Write(BinaryFormatMagicBytes, 0, BinaryFormatMagicBytes.Length);

        // Версия формата
        ms.WriteByte(BinaryFormatVersion);

        // Timestamp (long - ticks)
        ms.Write(BitConverter.GetBytes(timestamp.Ticks), 0, 8);

        // Level (byte)
        ms.WriteByte((byte)level);

        // Prefix: длина (int) + данные (UTF8)
        byte[] prefixBytes = Encoding.UTF8.GetBytes(prefix ?? string.Empty);
        ms.Write(BitConverter.GetBytes(prefixBytes.Length), 0, 4);
        if (prefixBytes.Length > 0)
        {
            ms.Write(prefixBytes, 0, prefixBytes.Length);
        }

        // Message: длина (int) + данные (UTF8)
        byte[] messageBytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
        ms.Write(BitConverter.GetBytes(messageBytes.Length), 0, 4);
        if (messageBytes.Length > 0)
        {
            ms.Write(messageBytes, 0, messageBytes.Length);
        }

        // PrevHash: длина (int) + данные (UTF8)
        byte[] prevHashBytes = Encoding.UTF8.GetBytes(prevHash ?? string.Empty);
        ms.Write(BitConverter.GetBytes(prevHashBytes.Length), 0, 4);
        if (prevHashBytes.Length > 0)
        {
            ms.Write(prevHashBytes, 0, prevHashBytes.Length);
        }

        // Hash: длина (int) + данные (UTF8)
        byte[] hashBytes = Encoding.UTF8.GetBytes(hash ?? string.Empty);
        ms.Write(BitConverter.GetBytes(hashBytes.Length), 0, 4);
        if (hashBytes.Length > 0)
        {
            ms.Write(hashBytes, 0, hashBytes.Length);
        }

        // TraceId: длина (int) + данные (UTF8)
        byte[] traceBytes = Encoding.UTF8.GetBytes(traceId ?? string.Empty);
        ms.Write(BitConverter.GetBytes(traceBytes.Length), 0, 4);
        if (traceBytes.Length > 0)
        {
            ms.Write(traceBytes, 0, traceBytes.Length);
        }

        // Properties: длина (int) + данные (UTF8)
        byte[] propsBytes = Encoding.UTF8.GetBytes(properties ?? string.Empty);
        ms.Write(BitConverter.GetBytes(propsBytes.Length), 0, 4);
        if (propsBytes.Length > 0)
        {
            ms.Write(propsBytes, 0, propsBytes.Length);
        }

        // Footer: RecordLength (int32) = вся запись включая footer
        int recordLength = checked((int)ms.Length + 4);
        ms.Write(BitConverter.GetBytes(recordLength), 0, 4);

        ms.Position = 0;
        await ms.CopyToAsync(stream);
    }

    /// <summary>
    /// Читает запись лога из бинарного формата.
    /// </summary>
    /// <param name="stream">Поток для чтения.</param>
    /// <returns>Запись лога или null, если чтение не удалось.</returns>
    private async Task<LogFileEntry> ReadBinaryLogEntryAsync(Stream stream)
    {
        try
        {
            long startPos = stream.CanSeek ? stream.Position : -1;

            // Проверяем magic number
            byte[] magicBuffer = new byte[4];
            int bytesRead = await stream.ReadAsync(magicBuffer, 0, 4);
            if (bytesRead != 4 || BitConverter.ToUInt32(magicBuffer, 0) != BinaryFormatMagic)
            {
                return null;
            }

            // Версия формата (v4-only)
            int version = stream.ReadByte();
            if (version == -1 || version != BinaryFormatVersion)
            {
                return null;
            }

            // Timestamp
            byte[] timestampBuffer = new byte[8];
            bytesRead = await stream.ReadAsync(timestampBuffer, 0, 8);
            if (bytesRead != 8) return null;
            DateTime timestamp = new DateTime(BitConverter.ToInt64(timestampBuffer, 0), DateTimeKind.Utc);

            // Level
            int levelByte = stream.ReadByte();
            if (levelByte == -1) return null;
            DeskLinkAuditLogLevel level = (DeskLinkAuditLogLevel)levelByte;

            // Prefix
            byte[] prefixLengthBuffer = new byte[4];
            bytesRead = await stream.ReadAsync(prefixLengthBuffer, 0, 4);
            if (bytesRead != 4) return null;
            int prefixLength = BitConverter.ToInt32(prefixLengthBuffer, 0);
            string prefix = string.Empty;
            if (prefixLength > 0)
            {
                if (prefixLength > 1024 * 1024) return null; // Защита от слишком больших значений
                byte[] prefixBuffer = new byte[prefixLength];
                bytesRead = await stream.ReadAsync(prefixBuffer, 0, prefixLength);
                if (bytesRead != prefixLength) return null;
                prefix = Encoding.UTF8.GetString(prefixBuffer);
            }

            // Message
            byte[] messageLengthBuffer = new byte[4];
            bytesRead = await stream.ReadAsync(messageLengthBuffer, 0, 4);
            if (bytesRead != 4) return null;
            int messageLength = BitConverter.ToInt32(messageLengthBuffer, 0);
            string message = string.Empty;
            if (messageLength > 0)
            {
                if (messageLength > 10 * 1024 * 1024) return null; // Защита от слишком больших значений (10MB)
                byte[] messageBuffer = new byte[messageLength];
                bytesRead = await stream.ReadAsync(messageBuffer, 0, messageLength);
                if (bytesRead != messageLength) return null;
                message = Encoding.UTF8.GetString(messageBuffer);
            }

            // PrevHash
            byte[] prevHashLengthBuffer = new byte[4];
            bytesRead = await stream.ReadAsync(prevHashLengthBuffer, 0, 4);
            if (bytesRead != 4) return null;
            int prevHashLength = BitConverter.ToInt32(prevHashLengthBuffer, 0);
            string prevHash = string.Empty;
            if (prevHashLength > 0)
            {
                if (prevHashLength > 1024) return null; // Хэш не может быть больше 1024 символов
                byte[] prevHashBuffer = new byte[prevHashLength];
                bytesRead = await stream.ReadAsync(prevHashBuffer, 0, prevHashLength);
                if (bytesRead != prevHashLength) return null;
                prevHash = Encoding.UTF8.GetString(prevHashBuffer);
            }

            // Hash
            byte[] hashLengthBuffer = new byte[4];
            bytesRead = await stream.ReadAsync(hashLengthBuffer, 0, 4);
            if (bytesRead != 4) return null;
            int hashLength = BitConverter.ToInt32(hashLengthBuffer, 0);
            string hash = string.Empty;
            if (hashLength > 0)
            {
                if (hashLength > 1024) return null; // Хэш не может быть больше 1024 символов
                byte[] hashBuffer = new byte[hashLength];
                bytesRead = await stream.ReadAsync(hashBuffer, 0, hashLength);
                if (bytesRead != hashLength) return null;
                hash = Encoding.UTF8.GetString(hashBuffer);
            }

            // TraceId
            byte[] traceLengthBuffer = new byte[4];
            bytesRead = await stream.ReadAsync(traceLengthBuffer, 0, 4);
            if (bytesRead != 4) return null;
            int traceLength = BitConverter.ToInt32(traceLengthBuffer, 0);
            string traceId = string.Empty;
            if (traceLength > 0)
            {
                if (traceLength > 1024) return null;
                byte[] traceBuffer = new byte[traceLength];
                bytesRead = await stream.ReadAsync(traceBuffer, 0, traceLength);
                if (bytesRead != traceLength) return null;
                traceId = Encoding.UTF8.GetString(traceBuffer);
            }

            // Properties
            byte[] propsLengthBuffer = new byte[4];
            bytesRead = await stream.ReadAsync(propsLengthBuffer, 0, 4);
            if (bytesRead != 4) return null;
            int propsLength = BitConverter.ToInt32(propsLengthBuffer, 0);
            string properties = string.Empty;
            if (propsLength > 0)
            {
                if (propsLength > 1024 * 1024) return null;
                byte[] propsBuffer = new byte[propsLength];
                bytesRead = await stream.ReadAsync(propsBuffer, 0, propsLength);
                if (bytesRead != propsLength) return null;
                properties = Encoding.UTF8.GetString(propsBuffer);
            }

            // Footer: RecordLength (int32)
            byte[] recordLenBuffer = new byte[4];
            bytesRead = await stream.ReadAsync(recordLenBuffer, 0, 4);
            if (bytesRead != 4) return null;
            int recordLen = BitConverter.ToInt32(recordLenBuffer, 0);
            if (recordLen <= 0) return null;
            if (startPos >= 0)
            {
                long actualLen = stream.Position - startPos;
                if (actualLen != recordLen)
                {
                    return null;
                }
            }

            return new LogFileEntry
            {
                Timestamp = timestamp,
                Level = level,
                Prefix = prefix,
                Message = message,
                PrevHash = prevHash,
                Hash = hash,
                TraceId = traceId,
                Properties = properties,
                IsTampered = false
            };
        }
        catch (Exception ex)
        {
            ReportInternalLoggerError("ReadBinaryLogEntryAsync", ex);
            return null;
        }
    }

    /// <summary>
    /// Проверяет, является ли сообщение ошибкой-дубликатом.
    /// </summary>
    /// <param name="message">Сообщение ошибки.</param>
    /// <returns>Кортеж: (является ли дубликатом, текущий счётчик, счётчик подавленных).</returns>
    private void CleanupErrorCacheIfNeeded(DateTime now)
    {
        var nowUtc = now.ToUniversalTime();
        if ((nowUtc - lastErrorCacheCleanupUtc).TotalSeconds < ErrorCacheCleanupMinIntervalSeconds)
        {
            return;
        }

        lastErrorCacheCleanupUtc = nowUtc;

        try
        {
            var keysToRemove = new List<string>();
            foreach (var kvp in errorCache)
            {
                if ((nowUtc - kvp.Value.LastSeen.ToUniversalTime()).TotalSeconds > ErrorCacheTtlSeconds)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                errorCache.Remove(key);
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Дедупликация ошибок: подавляем повторяющиеся ошибки и периодически пишем summary.
    /// </summary>
    private (bool IsDuplicate, bool ShouldLogSummary, int SuppressedForSummary) TrackDuplicate(string key, DateTime now)
    {
        // В одном writer-потоке можно работать без сложной атомарности.
        if (!errorCache.TryGetValue(key, out var entry) || (now - entry.LastSeen).TotalSeconds > DuplicateWindowSeconds)
        {
            errorCache[key] = new ErrorCacheEntry(1, now, 0, DateTime.MinValue);
            return (false, false, 0);
        }

        int newCount = entry.Count + 1;

        // До лимита — не duplicate
        if (newCount <= MaxDuplicateErrors)
        {
            errorCache[key] = entry with { Count = newCount, LastSeen = now };
            return (false, false, 0);
        }

        // Дубликат
        int suppressedSince = entry.SuppressedSinceSummary + 1;

        // Первое подавление — сразу логируем summary (как маркер, что началось подавление)
        if (newCount == MaxDuplicateErrors + 1)
        {
            errorCache[key] = new ErrorCacheEntry(newCount, now, 0, now);
            return (true, true, 1);
        }

        // Периодический summary раз в окно
        if (entry.LastSummaryLogged != DateTime.MinValue &&
            (now - entry.LastSummaryLogged).TotalSeconds >= DuplicateWindowSeconds)
        {
            errorCache[key] = new ErrorCacheEntry(newCount, now, 0, now);
            return (true, true, suppressedSince);
        }

        // Просто подавляем
        errorCache[key] = entry with { Count = newCount, LastSeen = now, SuppressedSinceSummary = suppressedSince };
        return (true, false, 0);
    }

    /// <summary>
    /// Получает все записи логов из всех файлов.
    /// </summary>
    /// <param name="service">Фильтр по сервису (Gateway, Weather и т.д.). null или пусто = все.</param>
    public async Task<List<LogFileEntry>> GetAllLogsAsync(string? service = null)
    {
        var files = GetAllLogFiles(service);
        var entries = new ConcurrentBag<LogFileEntry>();

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, ct) =>
        {
            var fileEntries = await ReadLogFileAsync(file);
            foreach (var entry in fileEntries)
            {
                entries.Add(entry);
            }
        });

        return entries.OrderByDescending(x => x.Timestamp).ToList();
    }

    /// <summary>
    /// Получает логи за указанную дату.
    /// </summary>
    /// <param name="date">Дата для фильтрации логов.</param>
    /// <param name="service">Фильтр по сервису (Gateway, Weather и т.д.). null или пусто = все.</param>
    public async Task<List<LogFileEntry>> GetLogsForDateAsync(DateTime date, string? service = null)
    {
        var files = GetLogFilesForDate(date, service);
        var entries = new ConcurrentBag<LogFileEntry>();

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, ct) =>
        {
            var fileEntries = await ReadLogFileAsync(file);
            foreach (var entry in fileEntries)
            {
                entries.Add(entry);
            }
        });

        return entries.OrderByDescending(x => x.Timestamp).ToList();
    }

    /// <summary>
    /// Поиск логов по уровню, тексту или дате.
    /// </summary>
    /// <param name="level">Уровень лога (опционально).</param>
    /// <param name="searchText">Текст для поиска (опционально).</param>
    /// <param name="date">Дата для фильтрации (опционально).</param>
    /// <returns>Список подходящих записей логов, отсортированных по времени (по убыванию).</returns>
    /// <summary>
    /// Поиск логов с использованием индексов для ускорения.
    /// </summary>
    /// <param name="level">Уровень лога (опционально).</param>
    /// <param name="searchText">Текст для поиска (опционально).</param>
    /// <param name="date">Дата для фильтрации (опционально).</param>
    /// <returns>Список подходящих записей логов, отсортированных по времени (по убыванию).</returns>
    public Task<List<LogFileEntry>> SearchLogsAsync(DeskLinkAuditLogLevel? level = null, string? searchText = null, DateTime? date = null, string? service = null)
    {
        HashSet<DeskLinkAuditLogLevel>? levels = null;
        if (level.HasValue)
        {
            levels = new HashSet<DeskLinkAuditLogLevel> { level.Value };
        }

        return SearchLogsAsync(levels, searchText, date, sourceContains: null, service);
    }

    /// <summary>
    /// Поиск логов с использованием индексов для ускорения.
    /// Поддерживает фильтрацию по нескольким уровням, по источнику (Prefix) и по сервису (Gateway, Weather и т.д.).
    /// </summary>
    public async Task<List<LogFileEntry>> SearchLogsAsync(
        IReadOnlyCollection<DeskLinkAuditLogLevel>? levels,
        string? searchText = null,
        DateTime? date = null,
        string? sourceContains = null,
        string? service = null)
    {
        var files = date.HasValue ? GetLogFilesForDate(date.Value, service) : GetAllLogFiles(service);
        var entries = new ConcurrentBag<LogFileEntry>();

        bool isDateTimeSearch = false;
        DateTime? searchDateTime = null;
        bool hasSeconds = false;
        bool hasOnlyTime = false;

        // Проверяем, является ли searchText трассой
        bool isTraceIdSearch = !string.IsNullOrEmpty(searchText) && searchText.Length >= 16 && !searchText.Contains(' ');

        if (!string.IsNullOrEmpty(searchText) && !isTraceIdSearch)
        {
            // Проверяем форматы с датой
            string[] dateTimeFormats =
            {
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd HH:mm",
                "yyyy-MM-dd H:mm:ss",
                "yyyy-MM-dd H:mm"
            };

            if (DateTime.TryParseExact(searchText, dateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDateTime))
            {
                isDateTimeSearch = true;
                searchDateTime = parsedDateTime;
                hasSeconds = searchText.Contains(":") && searchText.Split(':').Length == 3;
                hasOnlyTime = false;
            }
            else
            {
                // Проверяем форматы только времени (HH:mm:ss, HH:mm, H:mm:ss, H:mm)
                var timePattern = @"^(\d{1,2}):(\d{2})(?::(\d{2}))?$";
                var match = Regex.Match(searchText.Trim(), timePattern);
                if (match.Success)
                {
                    var hours = int.Parse(match.Groups[1].Value);
                    var minutes = int.Parse(match.Groups[2].Value);
                    var seconds = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;

                    if (hours >= 0 && hours < 24 && minutes >= 0 && minutes < 60 && seconds >= 0 && seconds < 60)
                    {
                        isDateTimeSearch = true;
                        // Используем текущую дату для сравнения (будет сравниваться только время)
                        searchDateTime = DateTime.Today.AddHours(hours).AddMinutes(minutes).AddSeconds(seconds);
                        hasSeconds = match.Groups[3].Success;
                        hasOnlyTime = true;
                    }
                }
            }
        }

        bool hasLevels = levels != null && levels.Count > 0;

        // Если есть фильтр по level(s) или TraceId, используем индекс для быстрого поиска
        bool useIndex = hasLevels || isTraceIdSearch;

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, ct) =>
        {
            List<LogFileEntry> fileEntries;

            if (useIndex)
            {
                // Используем индекс для фильтрации
                var indexEntries = await ReadIndexFileAsync(file);
                
                // Если индекс пуст или повреждён, перестроим его
                if (indexEntries.Count == 0 && File.Exists(file) && new FileInfo(file).Length > 0)
                {
                    await RebuildIndexAsync(file);
                    indexEntries = await ReadIndexFileAsync(file);
                }

                // Фильтруем по индексу
                var filteredOffsets = indexEntries
                    .Where(idx =>
                    {
                        bool levelMatch = !hasLevels || levels!.Contains(idx.Level);
                        bool traceMatch = !isTraceIdSearch || (idx.TraceId?.Contains(searchText!, StringComparison.OrdinalIgnoreCase) ?? false);
                        bool dateMatch = !date.HasValue || idx.Timestamp.Date == date.Value.Date;
                        return levelMatch && traceMatch && dateMatch;
                    })
                    .Select(idx => idx.FileOffset)
                    .ToList();

                // Читаем только отфильтрованные записи
                fileEntries = new List<LogFileEntry>();
                filteredOffsets.Sort();
                var svcName = GetServiceFromFilePath(file);
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    foreach (var offset in filteredOffsets)
                    {
                        if (offset < 0 || offset >= fs.Length) continue;
                        
                        fs.Position = offset;
                        var entry = await ReadBinaryLogEntryAsync(fs);
                        if (entry != null)
                        {
                            entry.Service = svcName;
                            fileEntries.Add(entry);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ReportInternalLoggerError("SearchLogsAsync.ReadByOffsets", ex, file);
                }
            }
            else
            {
                // Полное сканирование (если нет фильтров по индексу)
                fileEntries = await ReadLogFileAsync(file);
            }

            foreach (var entry in fileEntries)
            {
                // ДОБАВЛЕНО: Пропускаем, если entry полностью invalid (Message null не будет после изменений, но на всякий случай)
                if (string.IsNullOrEmpty(entry.Message) && entry.IsTampered) continue;

                // Проверяем, что запись соответствует выбранной дате
                bool matchesDate = !date.HasValue || entry.Timestamp.Date == date.Value.Date;

                bool matchesLevel = !hasLevels || levels!.Contains(entry.Level);
                bool matchesSource = string.IsNullOrWhiteSpace(sourceContains) ||
                                     (entry.Prefix?.Contains(sourceContains, StringComparison.OrdinalIgnoreCase) ?? false);
                bool matchesSearch = true;

                if (isDateTimeSearch && searchDateTime.HasValue)
                {
                    if (hasOnlyTime)
                    {
                        matchesSearch = entry.Timestamp.Hour == searchDateTime.Value.Hour &&
                                        entry.Timestamp.Minute == searchDateTime.Value.Minute &&
                                        (!hasSeconds || entry.Timestamp.Second == searchDateTime.Value.Second);
                    }
                    else
                    {
                        matchesSearch = entry.Timestamp.Year == searchDateTime.Value.Year &&
                                        entry.Timestamp.Month == searchDateTime.Value.Month &&
                                        entry.Timestamp.Day == searchDateTime.Value.Day &&
                                        entry.Timestamp.Hour == searchDateTime.Value.Hour &&
                                        entry.Timestamp.Minute == searchDateTime.Value.Minute &&
                                        (!hasSeconds || entry.Timestamp.Second == searchDateTime.Value.Second);
                    }
                }
                else if (!string.IsNullOrEmpty(searchText))
                {
                    matchesSearch =
                        (entry.Message?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (entry.Prefix?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (entry.TraceId?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (entry.Properties?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false);
                }

                if (matchesDate && matchesLevel && matchesSource && matchesSearch)
                {
                    entries.Add(entry);
                }
            }
        });

        return entries.OrderByDescending(x => x.Timestamp).ToList();
    }

    /// <summary>
    /// Читает одну запись лога по заданному смещению в файле.
    /// </summary>
    private async Task<LogFileEntry?> ReadLogEntryAtOffsetAsync(string filePath, long offset)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = offset;
            return await ReadBinaryLogEntryAsync(fs);
        }
        catch (Exception ex)
        {
            ReportInternalLoggerError("ReadLogEntryAtOffsetAsync", ex, filePath);
            return null;
        }
    }

    /// <summary>
    /// Подсчитывает количество логов за текущий день.
    /// </summary>
    /// <returns>Количество логов за сегодня.</returns>
    public async Task<int> GetTodayLogsCountAsync()
    {
        var today = DateTime.Today;
        var files = GetLogFilesForDate(today);
        int totalCount = 0;

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, ct) =>
        {
            int count = 0;
            if (File.Exists(file))
            {
                // Бинарный формат - считаем записи безопасно (используем реальный парсер записи)
                try
                {
                    using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    while (fileStream.Position < fileStream.Length)
                    {
                        var entry = await ReadBinaryLogEntryAsync(fileStream);
                        if (entry == null)
                        {
                            if (!TryResyncToNextMagic(fileStream))
                            {
                                break;
                            }
                            continue;
                        }

                        count++;
                    }
                }
                catch (Exception ex)
                {
                    // В случае ошибки возвращаем 0 для этого файла
                    ReportInternalLoggerError("GetTodayLogsCountAsync", ex, file);
                }
            }
            Interlocked.Add(ref totalCount, count);
        });

        return totalCount;
    }

    /// <summary>
    /// Подсчитывает количество логов за текущий день по указанному уровню.
    /// </summary>
    /// <param name="level">Уровень лога для подсчёта.</param>
    /// <returns>Количество логов за сегодня по уровню.</returns>
    public async Task<int> GetTodayLogsCountByLevelAsync(DeskLinkAuditLogLevel level)
    {
        var today = DateTime.Today;
        var files = GetLogFilesForDate(today);
        int totalCount = 0;

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, ct) =>
        {
            int count = 0;
            var entries = await ReadLogFileAsync(file);
            count = entries.Count(e => e.Level == level && !e.IsTampered); // ИЗМЕНЕНО: исключаем tampered для точности
            Interlocked.Add(ref totalCount, count);
        });

        return totalCount;
    }

    /// <summary>
    /// Получает путь к текущему файлу лога.
    /// </summary>
    /// <returns>Полный путь к файлу лога.</returns>
    private string GetLogFilePath()
    {
        string year = DateTime.UtcNow.ToString("yyyy");
        string month = DateTime.UtcNow.ToString("MM");
        string date = DateTime.UtcNow.ToString("yyyy-MM-dd");

        string yearDirectory = Path.Combine(logDirectory, year);
        string monthDirectory = Path.Combine(yearDirectory, month);

        if (!Directory.Exists(yearDirectory))
        {
            Directory.CreateDirectory(yearDirectory);
        }

        if (!Directory.Exists(monthDirectory))
        {
            Directory.CreateDirectory(monthDirectory);
        }

        return Path.Combine(monthDirectory, $"log-{date}.bin");
    }

    /// <summary>
    /// Выполняет ротацию файла лога, если он превысил максимальный размер.
    /// </summary>
    /// <param name="logFilePath">Путь к файлу лога.</param>
    private async Task RotateLogFileIfNeeded(string logFilePath)
    {
        FileInfo logFile = new(logFilePath);
        if (logFile.Exists && logFile.Length > MaxFileSizeInBytes)
        {
            string fileName = $"log-{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}.bin";
            string archiveFile = Path.Combine(logFile.DirectoryName, fileName);
            string indexPath = GetIndexFilePath(logFilePath);
            string archiveIndexPath = GetIndexFilePath(archiveFile);
            
            try
            {
                File.Move(logFilePath, archiveFile);

                // Перемещаем индексный файл вместе с логом
                if (File.Exists(indexPath))
                {
                    File.Move(indexPath, archiveIndexPath);
                }

                // После ротации текущий файл будет создан заново => сбрасываем кэш lastHash
                lastHashByFile.TryRemove(logFilePath, out _);

                // Минимальная видимость события ротации
                WriteBootstrapStderr("info", $"Rotated log file: '{logFilePath}' -> '{archiveFile}'");
            }
            catch (Exception ex)
            {
                ReportInternalLoggerError("RotateLogFileIfNeeded", ex, logFilePath);
            }
        }
    }

    /// <summary>
    /// Получает список файлов логов за указанную дату.
    /// </summary>
    /// <param name="date">Дата для фильтрации.</param>
    /// <returns>Список путей к файлам логов.</returns>
    private List<string> GetLogFilesForDate(DateTime date, string? service = null)
    {
        if (aggregateReadRoot != null)
        {
            return GetLogFilesForDateAggregate(date, service);
        }

        var yearDir = Path.Combine(logDirectory, date.ToString("yyyy"));
        var monthDir = Path.Combine(yearDir, date.ToString("MM"));
        if (!Directory.Exists(monthDir)) return new List<string>();

        string cacheKey = $"{date:yyyy-MM-dd}";
        return fileCache.GetOrAdd(cacheKey, _ =>
        {
            string datePrefix = $"log-{date:yyyy-MM-dd}";
            return Directory.GetFiles(monthDir, $"{datePrefix}*.bin").ToList();
        });
    }

    private List<string> GetLogFilesForDateAggregate(DateTime date, string? serviceFilter = null)
    {
        if (!Directory.Exists(aggregateReadRoot!)) return new List<string>();
        var result = new List<string>();
        string datePrefix = $"log-{date:yyyy-MM-dd}";
        var dirs = string.IsNullOrWhiteSpace(serviceFilter)
            ? Directory.GetDirectories(aggregateReadRoot!)
            : EnumerateServiceDirs(serviceFilter);
        foreach (var serviceDir in dirs)
        {
            var monthDir = Path.Combine(serviceDir, date.ToString("yyyy"), date.ToString("MM"));
            if (Directory.Exists(monthDir))
            {
                result.AddRange(Directory.GetFiles(monthDir, $"{datePrefix}*.bin"));
            }
        }
        return result;
    }

    /// <summary>
    /// Получает список всех файлов логов.
    /// </summary>
    /// <returns>Список путей к файлам логов.</returns>
    private List<string> GetAllLogFiles(string? service = null)
    {
        if (aggregateReadRoot != null)
        {
            return GetAllLogFilesAggregate(service);
        }

        var result = new List<string>();
        foreach (var year in Directory.GetDirectories(logDirectory))
        {
            foreach (var month in Directory.GetDirectories(year))
            {
                string cacheKey = Path.Combine(Path.GetFileName(year), Path.GetFileName(month));
                var monthFiles = fileCache.GetOrAdd(cacheKey, _ =>
                    Directory.GetFiles(month, "log-*.bin").ToList()
                );
                result.AddRange(monthFiles);
            }
        }
        return result;
    }

    private List<string> GetAllLogFilesAggregate(string? serviceFilter = null)
    {
        if (!Directory.Exists(aggregateReadRoot!)) return new List<string>();
        var result = new List<string>();
        var dirs = string.IsNullOrWhiteSpace(serviceFilter)
            ? Directory.GetDirectories(aggregateReadRoot!)
            : EnumerateServiceDirs(serviceFilter);
        foreach (var serviceDir in dirs)
        {
            if (!Directory.Exists(serviceDir)) continue;
            foreach (var year in Directory.GetDirectories(serviceDir))
            {
                foreach (var month in Directory.GetDirectories(year))
                {
                    result.AddRange(Directory.GetFiles(month, "log-*.bin"));
                }
            }
        }
        return result;
    }

    private IEnumerable<string> EnumerateServiceDirs(string serviceFilter)
    {
        var path = Path.Combine(aggregateReadRoot!, serviceFilter.Trim());
        if (Directory.Exists(path))
        {
            yield return path;
        }
    }

    /// <summary>
    /// Извлекает имя сервиса из пути к файлу лога.
    /// Структура: .../ServiceName/yyyy/MM/file.bin или .../logs/yyyy/MM/ (logs -> Gateway).
    /// </summary>
    private string GetServiceFromFilePath(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);      // .../MM
            if (string.IsNullOrEmpty(dir)) return "";
            var yearDir = Path.GetDirectoryName(dir);       // .../yyyy
            if (string.IsNullOrEmpty(yearDir)) return "";
            var serviceDir = Path.GetDirectoryName(yearDir); // .../ServiceName
            if (string.IsNullOrEmpty(serviceDir)) return "";
            var service = Path.GetFileName(serviceDir);
            return string.Equals(service, "logs", StringComparison.OrdinalIgnoreCase) ? "Relay" : service;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Очищает кэш списков файлов.
    /// </summary>
    private void InvalidateCache()
    {
        fileCache.Clear();
    }

    private void InvalidateCache(DateTime timestamp)
    {
        try
        {
            string dayKey = $"{timestamp:yyyy-MM-dd}";
            fileCache.TryRemove(dayKey, out _);

            string monthKey = Path.Combine(timestamp.ToString("yyyy"), timestamp.ToString("MM"));
            fileCache.TryRemove(monthKey, out _);

            // aggregate mode не кэширует, инвалидация не нужна
        }
        catch
        {
            fileCache.Clear();
        }
    }

    /// <summary>
    /// Асинхронно читает файл лога и проверяет цепочку хэшей.
    /// </summary>
    /// <param name="filePath">Путь к файлу лога.</param>
    /// <returns>Список записей логов с метками подмены.</returns>
    private async Task<List<LogFileEntry>> ReadLogFileAsync(string filePath)
    {
        var entries = new List<LogFileEntry>();
        if (!File.Exists(filePath)) return entries;

        // Бинарный формат
        return await ReadBinaryLogFileAsync(filePath);
    }

    /// <summary>
    /// Читает бинарный файл лога и проверяет цепочку хэшей.
    /// </summary>
    private async Task<List<LogFileEntry>> ReadBinaryLogFileAsync(string filePath)
    {
        var entries = new List<LogFileEntry>();
        string prevHash = "genesis";
        var serviceName = GetServiceFromFilePath(filePath);

        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            
            while (fileStream.Position < fileStream.Length)
            {
                var entry = await ReadBinaryLogEntryAsync(fileStream);
                
                if (entry == null)
                {
                    // Не удалось прочитать запись - помечаем как tampered
                    var tamperEntry = new LogFileEntry
                    {
                        Timestamp = DateTime.MinValue,
                        Level = DeskLinkAuditLogLevel.Error,
                        Message = "Неверный формат записи лога (бинарный)",
                        IsTampered = true,
                        TamperReason = "Ошибка чтения бинарной записи",
                        PrevHash = "invalid",
                        Hash = "invalid",
                        Service = serviceName
                    };
                    entries.Add(tamperEntry);
                    prevHash = "invalid";
                    
                    // Пытаемся найти следующую запись (ищем следующий magic number)
                    if (!TryResyncToNextMagic(fileStream))
                    {
                        break;
                    }
                    continue;
                }

                // Проверяем цепочку хэшей
                string storedPrevHash = entry.PrevHash ?? "";
                string storedHash = entry.Hash ?? "";

                if (string.IsNullOrEmpty(storedPrevHash) || string.IsNullOrEmpty(storedHash))
                {
                    entry.IsTampered = true;
                    entry.TamperReason = "Отсутствует PrevHash или Hash в записи";
                    entry.Service = serviceName;
                    entries.Add(entry);
                    prevHash = "invalid";
                    continue;
                }

                if (storedPrevHash != prevHash)
                {
                    entry.IsTampered = true;
                    entry.TamperReason = $"Цепочка нарушена - ожидался PrevHash: {prevHash}, найден: {storedPrevHash}";
                    entry.Service = serviceName;
                    entries.Add(entry);
                    prevHash = storedHash;
                    continue;
                }

                // Проверяем хэш записи
                string logContent = BuildLogContent(entry.Timestamp, entry.Level, entry.Prefix, entry.Message, entry.TraceId, entry.Properties);
                string computedHash = ComputeSHA256Hash(storedPrevHash + logContent);
                if (storedHash != computedHash)
                {
                    entry.IsTampered = true;
                    entry.TamperReason = $"Несоответствие хэша - вычисленный: {computedHash}, сохранённый: {storedHash}";
                    entry.Service = serviceName;
                    entries.Add(entry);
                    prevHash = storedHash;
                    continue;
                }

                prevHash = storedHash;
                entry.Service = serviceName;
                entries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            // В случае ошибки добавляем запись об ошибке
            ReportInternalLoggerError("ReadBinaryLogFileAsync", ex, filePath);
            entries.Add(new LogFileEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = DeskLinkAuditLogLevel.Error,
                Message = $"Ошибка чтения файла лога: {ex.Message}",
                IsTampered = true,
                TamperReason = "Исключение при чтении бинарного файла",
                PrevHash = "invalid",
                Hash = "invalid",
                Service = serviceName
            });
        }

        return entries;
    }

    /// <summary>
    /// Пытается перемотать поток на начало следующей записи (по magic number).
    /// Используется только при повреждениях/сбоях чтения.
    /// </summary>
    private bool TryResyncToNextMagic(Stream stream)
    {
        try
        {
            int matched = 0;
            while (stream.Position < stream.Length)
            {
                int b = stream.ReadByte();
                if (b == -1) break;

                if ((byte)b == BinaryFormatMagicBytes[matched])
                {
                    matched++;
                    if (matched == BinaryFormatMagicBytes.Length)
                    {
                        stream.Position -= BinaryFormatMagicBytes.Length;
                        return true;
                    }
                }
                else
                {
                    matched = ((byte)b == BinaryFormatMagicBytes[0]) ? 1 : 0;
                }
            }
        }
        catch (Exception ex)
        {
            ReportInternalLoggerError("TryResyncToNextMagic", ex);
        }

        return false;
    }

    /// <summary>
    /// Читает записи логов новее указанного момента (для синхронизации с сервером).
    /// </summary>
    public async Task<List<LogFileEntry>> ReadEntriesNewerThanAsync(DateTime sinceUtc, int maxCount = 500)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        var startDate = sinceUtc.Date;
        var endDate = DateTime.UtcNow.Date;
        var collected = new List<LogFileEntry>();

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            var files = GetLogFilesForDate(date);
            foreach (var file in files)
            {
                var fileEntries = await ReadLogFileAsync(file).ConfigureAwait(false);
                foreach (var entry in fileEntries)
                {
                    if (entry.Timestamp > sinceUtc)
                    {
                        collected.Add(entry);
                    }
                }
            }
        }

        return collected
            .OrderBy(x => x.Timestamp)
            .Take(maxCount)
            .ToList();
    }

    /// <summary>
    /// Удаляет локальные файлы логов старше cutoffDate, если они уже синхронизированы (minSyncedUtc).
    /// </summary>
    public int PruneLogFilesBeforeAsync(DateTime cutoffDate, DateTime minSyncedUtc)
    {
        if (aggregateReadRoot != null)
        {
            return 0;
        }

        var removed = 0;
        if (!Directory.Exists(logDirectory))
        {
            return removed;
        }

        foreach (var year in Directory.GetDirectories(logDirectory))
        {
            foreach (var month in Directory.GetDirectories(year))
            {
                foreach (var file in Directory.GetFiles(month, "log-*.bin"))
                {
                    if (!TryParseLogFileDate(file, out var fileDate))
                    {
                        continue;
                    }

                    if (fileDate >= cutoffDate.Date)
                    {
                        continue;
                    }

                    var dayEnd = fileDate.Date.AddDays(1).AddTicks(-1);
                    if (minSyncedUtc < dayEnd)
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(file);
                        var idx = Path.ChangeExtension(file, ".idx");
                        if (File.Exists(idx))
                        {
                            File.Delete(idx);
                        }

                        removed++;
                        InvalidateCache(fileDate);
                    }
                    catch (Exception ex)
                    {
                        ReportInternalLoggerError("PruneLogFilesBeforeAsync", ex, file);
                    }
                }
            }
        }

        return removed;
    }

    private static bool TryParseLogFileDate(string filePath, out DateTime fileDate)
    {
        fileDate = default;
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (!name.StartsWith("log-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var datePart = name["log-".Length..];
        var underscore = datePart.IndexOf('_');
        if (underscore >= 0)
        {
            datePart = datePart[..underscore];
        }

        return DateTime.TryParseExact(
            datePart,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out fileDate);
    }

}
