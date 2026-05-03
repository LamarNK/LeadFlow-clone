using System.IO;
using System.Text.Json;
using LeadFlow.Models;

namespace LeadFlow.Services;

public sealed class JsonSettingsService : ISettingsService
{
    private const string SettingsFileName = "LeadFlow.settings.dat";
    private const string DatabaseFileName = "leadflow.db";

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

        if (string.IsNullOrWhiteSpace(settings.DatabasePath))
        {
            settings.DatabasePath = GetDefaultDatabasePath();
        }

        settings.DemoModeEnabled = string.IsNullOrWhiteSpace(settings.Bitrix.WebhookUrl) || settings.DemoModeEnabled;
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        settings.DatabasePath = string.IsNullOrWhiteSpace(settings.DatabasePath) ? GetDefaultDatabasePath() : settings.DatabasePath;
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

    private static AppSettings CreateDefaults() => new()
    {
        DatabasePath = GetDefaultDatabasePath(),
        DemoModeEnabled = true,
        Bitrix = new BitrixSettings(),
        MonitoringSafety = new MonitoringSafetyOptions(),
        AvitoSelectors = new AvitoSelectorOptions(),
        Avito = new AvitoSettings()
    };
}
