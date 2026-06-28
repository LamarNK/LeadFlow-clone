using System.Text.Json;
using Orbita.Contracts;

namespace Orbita.Worker;

public sealed class WorkerUpdateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrbitaWorker",
            "update-result.json");

    private WorkerUpdateResultDto? _pendingHeartbeatResult;

    public void SaveResult(WorkerUpdateResultDto result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(result, JsonOptions));
        _pendingHeartbeatResult = result;
    }

    public WorkerUpdateResultDto? TryConsumePendingHeartbeatResult()
    {
        var pending = _pendingHeartbeatResult;
        _pendingHeartbeatResult = null;
        return pending;
    }

    public WorkerUpdateResultDto? LoadLastResult()
    {
        if (!File.Exists(StorePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkerUpdateResultDto>(File.ReadAllText(StorePath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}