using System.Globalization;
using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.LocalChrome;

/// <summary>
/// Снимает зависший Chrome с User Data: kill дерева процессов этого профиля и stale SingletonLock.
/// Не трогает обычный Chrome пользователя.
/// </summary>
public sealed class LocalChromeProfileReclaimer
{
    public static readonly string[] LockFileNames =
    [
        "SingletonLock",
        "SingletonCookie",
        "SingletonSocket",
        "lockfile"
    ];

    public const string DevToolsActivePortFileName = "DevToolsActivePort";

    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(8);

    private static readonly Regex PidRegex = new(@"\b(\d{2,10})\b", RegexOptions.CultureInvariant);

    private readonly ILocalChromeProcessHost _host;
    private readonly TimeSpan _wait;
    private readonly TimeSpan _poll;

    public LocalChromeProfileReclaimer()
        : this(LocalChromeOsProcessHost.Instance, DefaultWait, TimeSpan.FromMilliseconds(100))
    {
    }

    public LocalChromeProfileReclaimer(
        ILocalChromeProcessHost host,
        TimeSpan? wait = null,
        TimeSpan? poll = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _wait = wait is { } w && w > TimeSpan.Zero ? w : DefaultWait;
        _poll = poll is { } p && p > TimeSpan.Zero ? p : TimeSpan.FromMilliseconds(100);
    }

    public async Task<LocalChromeProfileReclaimResult> ReclaimAsync(
        string userDataDir,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userDataDir))
        {
            return LocalChromeProfileReclaimResult.Empty;
        }

        var dir = userDataDir.Trim();
        var killed = new List<int>();
        KillMatching(dir, killed);

        var staleLocks = DeleteStaleLockFiles(dir);
        await WaitUntilFreeAsync(dir, cancellationToken).ConfigureAwait(false);
        staleLocks += DeleteStaleLockFiles(dir);

        return new LocalChromeProfileReclaimResult
        {
            KilledProcessCount = killed.Count,
            StaleLockFilesRemoved = staleLocks,
            KilledProcessIds = killed,
            StillOccupied = IsOccupied(dir)
        };
    }

    public static bool CommandLineReferencesUserDataDir(string? commandLine, string userDataDir)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(userDataDir))
        {
            return false;
        }

        if (!commandLine.Contains("user-data-dir", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedDir = NormalizePath(userDataDir);
        if (normalizedDir.Length == 0)
        {
            return false;
        }

        return NormalizePath(commandLine).Contains(normalizedDir, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBrowserProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("chrome", StringComparison.OrdinalIgnoreCase)
            || name.Contains("chromium", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<int> ParseCandidatePids(string? lockText)
    {
        if (string.IsNullOrWhiteSpace(lockText))
        {
            return [];
        }

        var ids = new List<int>();
        foreach (Match match in PidRegex.Matches(lockText))
        {
            if (int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                && pid > 0
                && !ids.Contains(pid))
            {
                ids.Add(pid);
            }
        }

        return ids;
    }

    public static int? ParseDevToolsActivePort(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        var firstLine = lines[0].Trim();
        if (!int.TryParse(firstLine, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port <= 0
            || port > 65535)
        {
            return null;
        }

        return port;
    }

    private void KillMatching(string userDataDir, List<int> killed)
    {
        foreach (var pid in CollectTargetPids(userDataDir))
        {
            if (killed.Contains(pid))
            {
                continue;
            }

            try
            {
                _host.KillProcessTree(pid);
                killed.Add(pid);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private HashSet<int> CollectTargetPids(string userDataDir)
    {
        var browsers = _host.ListBrowserProcesses();
        var targets = new HashSet<int>();
        foreach (var process in browsers)
        {
            if (CommandLineReferencesUserDataDir(process.CommandLine, userDataDir))
            {
                targets.Add(process.ProcessId);
            }
        }

        foreach (var lockPath in _host.ListLockFilePaths(userDataDir))
        {
            if (!_host.FileExists(lockPath))
            {
                continue;
            }

            foreach (var pid in ParseCandidatePids(_host.TryReadText(lockPath)))
            {
                if (!_host.IsProcessAlive(pid))
                {
                    continue;
                }

                var process = browsers.FirstOrDefault(item => item.ProcessId == pid);
                if (process is null || !IsBrowserProcessName(process.Name))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(process.CommandLine)
                    && !CommandLineReferencesUserDataDir(process.CommandLine, userDataDir))
                {
                    continue;
                }

                targets.Add(pid);
            }
        }

        var port = ParseDevToolsActivePort(
            _host.TryReadText(Path.Combine(userDataDir, DevToolsActivePortFileName)));
        if (port is { } listening)
        {
            var pid = _host.FindPidListeningOnLocalPort(listening);
            if (pid is { } owner && _host.IsProcessAlive(owner))
            {
                targets.Add(owner);
            }
        }

        return targets;
    }

    private int DeleteStaleLockFiles(string userDataDir)
    {
        if (CollectTargetPids(userDataDir).Count > 0)
        {
            return 0;
        }

        var removed = 0;
        foreach (var path in _host.ListLockFilePaths(userDataDir))
        {
            if (!_host.FileExists(path))
            {
                continue;
            }

            _host.TryDeleteFile(path);
            if (!_host.FileExists(path))
            {
                removed++;
            }
        }

        var devTools = Path.Combine(userDataDir, DevToolsActivePortFileName);
        if (_host.FileExists(devTools))
        {
            _host.TryDeleteFile(devTools);
            if (!_host.FileExists(devTools))
            {
                removed++;
            }
        }

        return removed;
    }

    private bool IsOccupied(string userDataDir)
    {
        if (CollectTargetPids(userDataDir).Count > 0)
        {
            return true;
        }

        foreach (var path in _host.ListLockFilePaths(userDataDir))
        {
            if (_host.FileExists(path))
            {
                return true;
            }
        }

        return _host.FileExists(Path.Combine(userDataDir, DevToolsActivePortFileName));
    }

    private async Task WaitUntilFreeAsync(string userDataDir, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _wait;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CollectTargetPids(userDataDir).Count == 0)
            {
                return;
            }

            KillMatching(userDataDir, []);
            await Task.Delay(_poll, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string NormalizePath(string value)
    {
        var trimmed = value.Trim().Trim('"');
        return trimmed.Replace('/', '\\').TrimEnd('\\');
    }
}
