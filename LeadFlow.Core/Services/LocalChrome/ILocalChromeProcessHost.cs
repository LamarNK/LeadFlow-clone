namespace LeadFlow.Core.Services.LocalChrome;

/// <summary>
/// OS-доступ к процессам Chrome и lock-файлам профиля. Тесты подменяют адаптер, чтобы не трогать живой Chrome.
/// </summary>
public interface ILocalChromeProcessHost
{
    IReadOnlyList<LocalChromeOsProcess> ListBrowserProcesses();

    bool IsProcessAlive(int processId);

    void KillProcessTree(int processId);

    IReadOnlyList<string> ListLockFilePaths(string userDataDir);

    bool FileExists(string path);

    string? TryReadText(string path);

    void TryDeleteFile(string path);
}

public sealed record LocalChromeOsProcess(int ProcessId, string Name, string? CommandLine);

public sealed class LocalChromeProfileReclaimResult
{
    public static LocalChromeProfileReclaimResult Empty { get; } = new();

    public int KilledProcessCount { get; init; }

    public int StaleLockFilesRemoved { get; init; }

    public IReadOnlyList<int> KilledProcessIds { get; init; } = [];
}
