using System.IO;
using System.Security.Cryptography;
using System.Text.Json;


namespace LeadFlow.Services;

public sealed class JsonSettingsService(string? dataDirectoryOverride = null) : ISettingsService
{
    private const string SettingsFileName = "LeadFlow.settings.dat";
    private const string DatabaseFileName = "leadflow.db";

    private readonly string _dataDirectoryPath = dataDirectoryOverride ?? Path.Combine(AppContext.BaseDirectory, "Data");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            var defaults = CreateDefaults(_dataDirectoryPath);
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        AppSettings settings;
        await using (var stream = File.OpenRead(path))
        {
            var bytes = EncryptedSettingsSerializer.DecryptFromStream(
                stream,
                SettingsEncryptionKeyHelper.GetDefaultKey(),
                userPassword: null);
            settings = JsonSerializer.Deserialize<AppSettings>(bytes, SerializerOptions) ?? CreateDefaults(_dataDirectoryPath);
        }

        NormalizeSettings(settings, _dataDirectoryPath);
        return settings;
    }

    /// <summary>Generates and persists <see cref="AppSettings.DatabaseEncryptionKey"/> when missing.</summary>
    public async Task EnsureDatabaseEncryptionKeyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.DatabaseEncryptionKey))
        {
            return;
        }

        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        settings.DatabaseEncryptionKey = Convert.ToBase64String(bytes);
        await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        NormalizeSettings(settings, _dataDirectoryPath);
        var path = GetSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var storageSnapshot = CreateStorageSnapshot(settings);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(storageSnapshot, SerializerOptions);
        await using var stream = File.Create(path);
        EncryptedSettingsSerializer.EncryptToStream(stream, bytes, SettingsEncryptionKeyHelper.GetDefaultKey(), userPassword: null);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public string GetSettingsPath() =>
        Path.Combine(_dataDirectoryPath, SettingsFileName);

    public static async Task<AppSettings> LoadFromDataDirectoryAsync(
        string dataDirectory,
        CancellationToken cancellationToken = default)
    {
        var service = new JsonSettingsService(dataDirectory);
        return await service.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string GetDefaultDatabasePath() =>
        Path.Combine(GetDataDirectoryPath(), DatabaseFileName);

    public static string GetDataDirectoryPath() =>
        Path.Combine(AppContext.BaseDirectory, "Data");

    internal static void NormalizeSettings(AppSettings settings) =>
        NormalizeSettings(settings, GetDataDirectoryPath());

    internal static void NormalizeSettings(AppSettings settings, string dataDirectoryPath)
    {
        settings.DatabasePath = Path.Combine(dataDirectoryPath, DatabaseFileName);
        settings.DemoModeEnabled = false;
        settings.Bitrix ??= new BitrixSettings();
        settings.Bitrix.WebhookUrl = settings.Bitrix.WebhookUrl?.Trim() ?? string.Empty;
        settings.Bitrix.DealIdempotencyUfCode = settings.Bitrix.DealIdempotencyUfCode?.Trim() ?? string.Empty;
        settings.Bitrix.DealAgeUfCode = settings.Bitrix.DealAgeUfCode?.Trim() ?? string.Empty;
        settings.Bitrix.DealProfessionUfCode = settings.Bitrix.DealProfessionUfCode?.Trim() ?? string.Empty;
        settings.Bitrix.DealCityUfCode = settings.Bitrix.DealCityUfCode?.Trim() ?? string.Empty;
        settings.MonitoringSafety ??= new MonitoringSafetyOptions();
        settings.MonitoringSafety.CheckIntervalSeconds = Math.Clamp(settings.MonitoringSafety.CheckIntervalSeconds, 30, 3600);
        settings.MonitoringSafety.MaxConcurrentAccounts = Math.Clamp(settings.MonitoringSafety.MaxConcurrentAccounts, 1, 10);
        settings.AvitoSelectors = new AvitoSelectorOptions();
        settings.Avito ??= new AvitoSettings();
    }

    private static AppSettings CreateDefaults(string dataDirectoryPath) => new()
    {
        DatabasePath = Path.Combine(dataDirectoryPath, DatabaseFileName),
        DemoModeEnabled = false,
        Bitrix = new BitrixSettings(),
        MonitoringSafety = new MonitoringSafetyOptions(),
        AvitoSelectors = new AvitoSelectorOptions(),
        Avito = new AvitoSettings()
    };

    private static AppSettings CreateStorageSnapshot(AppSettings settings) => new()
    {
        DemoModeEnabled = settings.DemoModeEnabled,
        DatabasePath = settings.DatabasePath,
        DatabaseEncryptionKey = settings.DatabaseEncryptionKey,
        DuplicateScope = settings.DuplicateScope,
        MonitoringSafety = new MonitoringSafetyOptions
        {
            CheckIntervalSeconds = settings.MonitoringSafety.CheckIntervalSeconds,
            StopOnCaptcha = settings.MonitoringSafety.StopOnCaptcha,
            StopOnAuthRequired = settings.MonitoringSafety.StopOnAuthRequired,
            AutoStartMonitoring = settings.MonitoringSafety.AutoStartMonitoring,
            MaxConcurrentAccounts = settings.MonitoringSafety.MaxConcurrentAccounts
        },
        Bitrix = new BitrixSettings
        {
            WebhookUrl = settings.Bitrix.WebhookUrl,
            EntityType = settings.Bitrix.EntityType,
            ResponsibleId = settings.Bitrix.ResponsibleId,
            LeadSource = settings.Bitrix.LeadSource,
            CheckDuplicatesInBitrix = settings.Bitrix.CheckDuplicatesInBitrix,
            DealIdempotencyUfCode = settings.Bitrix.DealIdempotencyUfCode,
            DealAgeUfCode = settings.Bitrix.DealAgeUfCode,
            DealProfessionUfCode = settings.Bitrix.DealProfessionUfCode,
            DealCityUfCode = settings.Bitrix.DealCityUfCode
        },
        AvitoSelectors = new AvitoSelectorOptions
        {
            ResponseListSelector = settings.AvitoSelectors.ResponseListSelector,
            ResponseItemSelector = settings.AvitoSelectors.ResponseItemSelector,
            FullNameSelector = settings.AvitoSelectors.FullNameSelector,
            PhoneSelector = settings.AvitoSelectors.PhoneSelector,
            CitySelector = settings.AvitoSelectors.CitySelector,
            VacancySelector = settings.AvitoSelectors.VacancySelector,
            AgeSelector = settings.AvitoSelectors.AgeSelector,
            SourceLinkSelector = settings.AvitoSelectors.SourceLinkSelector,
            ExtractionScript = settings.AvitoSelectors.ExtractionScript,
            LastSuccessfulUseAt = settings.AvitoSelectors.LastSuccessfulUseAt
        },
        Avito = new AvitoSettings()
    };
}
