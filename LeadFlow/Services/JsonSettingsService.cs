using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LeadFlow.Models;

namespace LeadFlow.Services;

public sealed class JsonSettingsService(string? dataDirectoryOverride = null) : ISettingsService
{
    private const string SettingsFileName = "LeadFlow.settings.dat";
    private const string DatabaseFileName = "leadflow.db";
    public const string FixedBitrixWebhookUrl = "https://b24-l7qyiy.bitrix24.ru/rest/22/i6l8tl41e71kmj5o/";

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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions);
        await using var stream = File.Create(path);
        EncryptedSettingsSerializer.EncryptToStream(stream, bytes, SettingsEncryptionKeyHelper.GetDefaultKey(), userPassword: null);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public string GetSettingsPath() =>
        Path.Combine(_dataDirectoryPath, SettingsFileName);

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
        settings.Bitrix.WebhookUrl = FixedBitrixWebhookUrl;
        settings.MonitoringSafety ??= new MonitoringSafetyOptions();
        settings.MonitoringSafety.CheckIntervalSeconds = Math.Clamp(settings.MonitoringSafety.CheckIntervalSeconds, 30, 3600);
        settings.AvitoSelectors = new AvitoSelectorOptions();
        settings.Avito ??= new AvitoSettings();
    }

    private static AppSettings CreateDefaults(string dataDirectoryPath) => new()
    {
        DatabasePath = Path.Combine(dataDirectoryPath, DatabaseFileName),
        DemoModeEnabled = false,
        Bitrix = new BitrixSettings
        {
            WebhookUrl = FixedBitrixWebhookUrl
        },
        MonitoringSafety = new MonitoringSafetyOptions(),
        AvitoSelectors = new AvitoSelectorOptions(),
        Avito = new AvitoSettings()
    };
}
