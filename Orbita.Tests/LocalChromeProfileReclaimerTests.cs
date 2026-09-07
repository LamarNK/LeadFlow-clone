using LeadFlow.Core.Services.LocalChrome;

namespace Orbita.Tests;

public sealed class LocalChromeProfileReclaimerTests
{
    private const string ProfileDir = @"C:\Users\Admin\AppData\Local\Orbita\ChromeProfiles\acc-1";

    [Fact]
    public void CommandLineReferencesUserDataDir_RequiresFlagAndPath()
    {
        Assert.True(LocalChromeProfileReclaimer.CommandLineReferencesUserDataDir(
            $@"chrome.exe --user-data-dir={ProfileDir} --no-first-run",
            ProfileDir));
        Assert.True(LocalChromeProfileReclaimer.CommandLineReferencesUserDataDir(
            $@"chrome.exe --user-data-dir=""{ProfileDir}""",
            ProfileDir));
        Assert.False(LocalChromeProfileReclaimer.CommandLineReferencesUserDataDir(
            @"C:\Program Files\Google\Chrome\Application\chrome.exe --type=renderer",
            ProfileDir));
        Assert.False(LocalChromeProfileReclaimer.CommandLineReferencesUserDataDir(
            $@"chrome.exe {ProfileDir}",
            ProfileDir));
    }

    [Fact]
    public async Task Reclaim_StaleLockDeadPid_DeletesFilesWithoutKill()
    {
        var host = new FakeHost();
        host.Files[Path.Combine(ProfileDir, "SingletonLock")] = "host-4242";
        host.Files[Path.Combine(ProfileDir, "SingletonCookie")] = "cookie";
        var sut = new LocalChromeProfileReclaimer(host, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));

        var result = await sut.ReclaimAsync(ProfileDir);

        Assert.Equal(0, result.KilledProcessCount);
        Assert.Empty(host.Killed);
        Assert.False(host.FileExists(Path.Combine(ProfileDir, "SingletonLock")));
        Assert.True(result.StaleLockFilesRemoved >= 2);
    }

    [Fact]
    public async Task Reclaim_LiveChromePidInLock_KillsTree()
    {
        var host = new FakeHost();
        host.Alive.Add(9100);
        host.Browsers.Add(new LocalChromeOsProcess(
            9100,
            "chrome",
            $@"chrome.exe --user-data-dir={ProfileDir} --type=browser"));
        host.Files[Path.Combine(ProfileDir, "SingletonLock")] = "DESKTOP-9100";
        var sut = new LocalChromeProfileReclaimer(host, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));

        var result = await sut.ReclaimAsync(ProfileDir);

        Assert.Equal(1, result.KilledProcessCount);
        Assert.Equal([9100], host.Killed);
        Assert.False(host.FileExists(Path.Combine(ProfileDir, "SingletonLock")));
    }

    [Fact]
    public async Task Reclaim_MatchingCommandLine_KillsEvenWithoutLock()
    {
        var host = new FakeHost();
        host.Alive.Add(8801);
        host.Browsers.Add(new LocalChromeOsProcess(
            8801,
            "chrome",
            $@"C:\Program Files\Google\Chrome\Application\chrome.exe --user-data-dir={ProfileDir}"));
        var sut = new LocalChromeProfileReclaimer(host, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));

        var result = await sut.ReclaimAsync(ProfileDir);

        Assert.Equal([8801], host.Killed);
        Assert.Equal(1, result.KilledProcessCount);
    }

    [Fact]
    public async Task Reclaim_DoesNotKillUnrelatedChrome()
    {
        var host = new FakeHost();
        host.Alive.Add(100);
        host.Browsers.Add(new LocalChromeOsProcess(
            100,
            "chrome",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe --user-data-dir=C:\Users\Admin\AppData\Local\Google\Chrome\User Data"));
        host.Files[Path.Combine(ProfileDir, "SingletonLock")] = "100";
        var sut = new LocalChromeProfileReclaimer(host, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));

        var result = await sut.ReclaimAsync(ProfileDir);

        Assert.Empty(host.Killed);
        Assert.Equal(0, result.KilledProcessCount);
        Assert.False(host.FileExists(Path.Combine(ProfileDir, "SingletonLock")));
    }

    [Fact]
    public async Task Reclaim_LockPidChromeWithoutCommandLine_Kills()
    {
        var host = new FakeHost();
        host.Alive.Add(7700);
        host.Browsers.Add(new LocalChromeOsProcess(7700, "chrome", CommandLine: null));
        host.Files[Path.Combine(ProfileDir, "SingletonLock")] = "7700";
        var sut = new LocalChromeProfileReclaimer(host, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));

        var result = await sut.ReclaimAsync(ProfileDir);

        Assert.Equal([7700], host.Killed);
        Assert.Equal(1, result.KilledProcessCount);
    }

    [Fact]
    public void ParseDevToolsActivePort_ReadsFirstLine()
    {
        Assert.Equal(9222, LocalChromeProfileReclaimer.ParseDevToolsActivePort("9222\n/devtools/browser/abc"));
        Assert.Null(LocalChromeProfileReclaimer.ParseDevToolsActivePort("not-a-port"));
        Assert.Null(LocalChromeProfileReclaimer.ParseDevToolsActivePort(""));
    }

    [Fact]
    public async Task Reclaim_DevToolsActivePort_KillsListenerPid()
    {
        var host = new FakeHost();
        host.Alive.Add(3311);
        host.PortOwners[9333] = 3311;
        host.Files[Path.Combine(ProfileDir, LocalChromeProfileReclaimer.DevToolsActivePortFileName)] =
            "9333\n/devtools/browser/xyz";
        var sut = new LocalChromeProfileReclaimer(host, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));

        var result = await sut.ReclaimAsync(ProfileDir);

        Assert.Equal([3311], host.Killed);
        Assert.False(host.FileExists(Path.Combine(ProfileDir, LocalChromeProfileReclaimer.DevToolsActivePortFileName)));
        Assert.False(result.StillOccupied);
    }

    [Fact]
    public async Task Reclaim_DevToolsPortOfUnrelatedProcess_DoesNotKillWhenPidDeadAfterLookup()
    {
        var host = new FakeHost();
        host.PortOwners[9444] = 12;
        host.Files[Path.Combine(ProfileDir, LocalChromeProfileReclaimer.DevToolsActivePortFileName)] = "9444\n";
        var sut = new LocalChromeProfileReclaimer(host, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(5));

        var result = await sut.ReclaimAsync(ProfileDir);

        Assert.Empty(host.Killed);
        Assert.Equal(0, result.KilledProcessCount);
    }

    private sealed class FakeHost : ILocalChromeProcessHost
    {
        public List<LocalChromeOsProcess> Browsers { get; } = [];

        public HashSet<int> Alive { get; } = [];

        public List<int> Killed { get; } = [];

        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<LocalChromeOsProcess> ListBrowserProcesses() => Browsers;

        public bool IsProcessAlive(int processId) => Alive.Contains(processId);

        public void KillProcessTree(int processId)
        {
            Killed.Add(processId);
            Alive.Remove(processId);
            Browsers.RemoveAll(item => item.ProcessId == processId);
        }

        public IReadOnlyList<string> ListLockFilePaths(string userDataDir) =>
            LocalChromeProfileReclaimer.LockFileNames
                .Select(name => Path.Combine(userDataDir, name))
                .ToArray();

        public bool FileExists(string path) => Files.ContainsKey(path);

        public string? TryReadText(string path) => Files.TryGetValue(path, out var text) ? text : null;

        public void TryDeleteFile(string path) => Files.Remove(path);

        public Dictionary<int, int> PortOwners { get; } = [];

        public int? FindPidListeningOnLocalPort(int port) =>
            PortOwners.TryGetValue(port, out var pid) ? pid : null;
    }
}
