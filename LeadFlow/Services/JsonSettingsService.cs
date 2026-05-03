using System.IO;
using System.Text.Json;
using LeadFlow.Models;

namespace LeadFlow.Services;

public sealed class JsonSettingsService : ISettingsService
{
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

        await using var stream = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? CreateDefaults();

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
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public string GetSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeadFlow", "settings.json");

    public static string GetDefaultDatabasePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeadFlow", "leadflow.db");

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
