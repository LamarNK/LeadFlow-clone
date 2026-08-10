using System.Text.Json;

namespace Orbita.Worker.Services;

public sealed class WorkerLogSyncState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _statePath;

    public WorkerLogSyncState()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrbitaWorker");
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "log-sync-state.json");
    }

    public DateTime LastSyncedUtc { get; private set; } = DateTime.UtcNow.AddDays(-1);

    public void Load()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            var state = JsonSerializer.Deserialize<StateDto>(json, JsonOptions);
            if (state?.LastSyncedUtc is { } synced)
            {
                // JSON без Z часто приходит Unspecified — считаем UTC (так и пишем).
                LastSyncedUtc = synced.Kind switch
                {
                    DateTimeKind.Utc => synced,
                    DateTimeKind.Local => synced.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(synced, DateTimeKind.Utc)
                };
            }
        }
        catch
        {
            // keep default
        }
    }

    public void Save(DateTime lastSyncedUtc)
    {
        LastSyncedUtc = lastSyncedUtc;
        var dto = new StateDto { LastSyncedUtc = lastSyncedUtc };
        File.WriteAllText(_statePath, JsonSerializer.Serialize(dto, JsonOptions));
    }

    private sealed class StateDto
    {
        public DateTime LastSyncedUtc { get; set; }
    }
}