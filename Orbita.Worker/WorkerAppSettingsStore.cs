using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeadFlow.Core.Models;
using Microsoft.Data.Sqlite;
using LeadFlow.Core.Data;

namespace Orbita.Worker;

public sealed class WorkerAppSettingsStore
{
    private const string SettingsFileName = "app-settings.dat";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string DataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrbitaWorker",
            "Data");

    private static string SettingsPath => Path.Combine(DataDirectory, SettingsFileName);

    public AppSettings LoadOrCreate()
    {
        Directory.CreateDirectory(DataDirectory);

        var settings = File.Exists(SettingsPath) ? Load() : CreateDefaults();
        Normalize(settings);

        if (EnsureDatabaseEncryptionKey(settings))
        {
            Save(settings);
        }

        RecoverInaccessibleDatabaseIfNeeded(settings);
        return settings;
    }

    internal AppSettings Load()
    {
        var protectedBytes = File.ReadAllBytes(SettingsPath);
        var json = Encoding.UTF8.GetString(
            ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? CreateDefaults();
        Normalize(settings);
        return settings;
    }

    internal void Save(AppSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(DataDirectory);
        var json = JsonSerializer.Serialize(CreateStorageSnapshot(settings), JsonOptions);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json),
            null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SettingsPath, protectedBytes);
    }

    internal static bool EnsureDatabaseEncryptionKey(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DatabaseEncryptionKey))
        {
            return false;
        }

        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        settings.DatabaseEncryptionKey = Convert.ToBase64String(bytes);
        return true;
    }

    private static void RecoverInaccessibleDatabaseIfNeeded(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DatabasePath) || !File.Exists(settings.DatabasePath))
        {
            return;
        }

        if (CanOpenDatabase(settings))
        {
            return;
        }

        var backupPath = $"{settings.DatabasePath}.bak.{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(settings.DatabasePath, backupPath, overwrite: false);
    }

    private static bool CanOpenDatabase(AppSettings settings)
    {
        try
        {
            using var connection = new SqliteConnection(
                EncryptedSqliteConnectionBuilder.BuildConnectionString(settings));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }
        catch
        {
            return false;
        }
    }

    private static AppSettings CreateDefaults() =>
        new()
        {
            DuplicateScope = DuplicateScope.GlobalAcrossAllAccounts,
            MonitoringSafety = new MonitoringSafetyOptions { MaxConcurrentAccounts = 1 }
        };

    private static AppSettings CreateStorageSnapshot(AppSettings settings) =>
        new()
        {
            DatabasePath = settings.DatabasePath,
            DatabaseEncryptionKey = settings.DatabaseEncryptionKey,
            DuplicateScope = settings.DuplicateScope,
            MonitoringSafety = new MonitoringSafetyOptions
            {
                CheckIntervalSeconds = settings.MonitoringSafety.CheckIntervalSeconds,
                StopOnCaptcha = settings.MonitoringSafety.StopOnCaptcha,
                StopOnAuthRequired = settings.MonitoringSafety.StopOnAuthRequired,
                MaxConcurrentAccounts = settings.MonitoringSafety.MaxConcurrentAccounts
            }
        };

    private static void Normalize(AppSettings settings)
    {
        settings.DatabasePath = Path.Combine(DataDirectory, "cache.db");
        settings.DemoModeEnabled = false;
        settings.MonitoringSafety ??= new MonitoringSafetyOptions();
        settings.MonitoringSafety.MaxConcurrentAccounts =
            Math.Clamp(settings.MonitoringSafety.MaxConcurrentAccounts, 1, 10);
        settings.MonitoringSafety.CheckIntervalSeconds =
            Math.Clamp(settings.MonitoringSafety.CheckIntervalSeconds, 30, 3600);
        settings.Avito ??= new AvitoSettings();
        settings.Bitrix ??= new BitrixSettings();
    }
}