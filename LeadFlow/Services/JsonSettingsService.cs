using System.IO;
using System.Text.Json;
using LeadFlow.Models;

namespace LeadFlow.Services;

public sealed class JsonSettingsService : ISettingsService
{
    private const string SettingsFileName = "LeadFlow.settings.dat";
    private const string DatabaseFileName = "leadflow.db";
    public const string FixedBitrixWebhookUrl = "https://b24-l7qyiy.bitrix24.ru/rest/22/i6l8tl41e71kmj5o/";

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
            var defaults = CreateDefaults();
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
            settings = JsonSerializer.Deserialize<AppSettings>(bytes, SerializerOptions) ?? CreateDefaults();
        }

        NormalizeSettings(settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        NormalizeSettings(settings);
        var path = GetSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions);
        await using var stream = File.Create(path);
        EncryptedSettingsSerializer.EncryptToStream(stream, bytes, SettingsEncryptionKeyHelper.GetDefaultKey(), userPassword: null);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public string GetSettingsPath() =>
        Path.Combine(GetDataDirectoryPath(), SettingsFileName);

    public static string GetDefaultDatabasePath() =>
        Path.Combine(GetDataDirectoryPath(), DatabaseFileName);

    public static string GetDataDirectoryPath() =>
        Path.Combine(AppContext.BaseDirectory, "Data");

    private static void NormalizeSettings(AppSettings settings)
    {
        settings.DatabasePath = GetDefaultDatabasePath();
        settings.DemoModeEnabled = false;
        settings.Bitrix ??= new BitrixSettings();
        settings.Bitrix.WebhookUrl = FixedBitrixWebhookUrl;
        settings.MonitoringSafety ??= new MonitoringSafetyOptions();
        settings.MonitoringSafety.CheckIntervalSeconds = 60;
        if (settings.MonitoringSafety.ActiveAdsRefreshIntervalMinutes is < 5 or > 240)
        {
            settings.MonitoringSafety.ActiveAdsRefreshIntervalMinutes = 45;
        }
        settings.AvitoSelectors = new AvitoSelectorOptions();
        settings.Avito ??= new AvitoSettings();
    }

    private static AppSettings CreateDefaults() => new()
    {
        DatabasePath = GetDefaultDatabasePath(),
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
