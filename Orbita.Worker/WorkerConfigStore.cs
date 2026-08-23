using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbita.Worker;

public sealed class WorkerConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrbitaWorker");

    private static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static string LogsDirectory => Path.Combine(ConfigDirectory, "logs");

    public bool IsInstalled => IsConfigured(Load());

    public static bool IsConfigured(WorkerCredentials credentials) =>
        !string.IsNullOrWhiteSpace(credentials.ApiKey);

    public static WorkerCredentials CreateCredentials(string apiKey) =>
        new()
        {
            ApiBaseUrl = WorkerSetupConstants.ApiBaseUrl,
            ApiKey = apiKey.Trim()
        };

    public WorkerCredentials Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new WorkerCredentials { ApiBaseUrl = WorkerSetupConstants.ApiBaseUrl };
        }

        var protectedBytes = File.ReadAllBytes(ConfigPath);
        var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
        var credentials = JsonSerializer.Deserialize<WorkerCredentials>(json, JsonOptions) ?? new WorkerCredentials();
        credentials.ApiBaseUrl = WorkerSetupConstants.ApiBaseUrl;
        return credentials;
    }

    public void Save(WorkerCredentials credentials)
    {
        credentials.ApiBaseUrl = WorkerSetupConstants.ApiBaseUrl;
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogsDirectory);
        var json = JsonSerializer.Serialize(credentials, JsonOptions);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(ConfigPath, protectedBytes);
    }
}