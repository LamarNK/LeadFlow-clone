using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LeadFlow.Core.Services.LocalChrome;

/// <summary>Реальный host: chrome/chromium в системе и lock-файлы User Data.</summary>
public sealed class LocalChromeOsProcessHost : ILocalChromeProcessHost
{
    public static LocalChromeOsProcessHost Instance { get; } = new();

    public IReadOnlyList<LocalChromeOsProcess> ListBrowserProcesses()
    {
        var result = new List<LocalChromeOsProcess>();
        foreach (var name in new[] { "chrome", "chromium" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    result.Add(new LocalChromeOsProcess(
                        process.Id,
                        process.ProcessName,
                        LocalChromeProcessCommandLine.TryGet(process)));
                }
                catch
                {
                    // Skip processes we cannot inspect.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return result;
    }

    public bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public void KillProcessTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort: process may have already exited.
        }
    }

    public IReadOnlyList<string> ListLockFilePaths(string userDataDir)
    {
        return LocalChromeProfileReclaimer.LockFileNames
            .Select(name => Path.Combine(userDataDir, name))
            .ToArray();
    }

    public bool FileExists(string path) => File.Exists(path);

    public string? TryReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    public void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Lock file may still be held.
        }
    }
}

internal static class LocalChromeProcessCommandLine
{
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    public static string? TryGet(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryGetWindows(process);
        }

        return TryGetUnix(process.Id);
    }

    private static string? TryGetUnix(int processId)
    {
        try
        {
            var path = $"/proc/{processId}/cmdline";
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return null;
            }

            return System.Text.Encoding.UTF8.GetString(bytes).Replace('\0', ' ').Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetWindows(Process process)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var handle = process.Handle;
            var status = NtQueryInformationProcess(
                handle,
                ProcessCommandLineInformation,
                IntPtr.Zero,
                0,
                out var length);
            if (length <= 0 && status != StatusInfoLengthMismatch)
            {
                return null;
            }

            if (length <= 0)
            {
                length = 512;
            }

            buffer = Marshal.AllocHGlobal(length);
            status = NtQueryInformationProcess(
                handle,
                ProcessCommandLineInformation,
                buffer,
                length,
                out _);
            if (status != 0)
            {
                return null;
            }

            var unicode = Marshal.PtrToStructure<UnicodeString>(buffer);
            if (unicode.Buffer == IntPtr.Zero || unicode.Length == 0)
            {
                return null;
            }

            return Marshal.PtrToStringUni(unicode.Buffer, unicode.Length / 2);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }
}
