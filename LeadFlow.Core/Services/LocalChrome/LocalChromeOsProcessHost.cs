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

    public int? FindPidListeningOnLocalPort(int port)
    {
        if (port <= 0 || port > 65535 || !OperatingSystem.IsWindows())
        {
            return null;
        }

        return LocalChromeTcpTable.FindPidListeningOnLocalPort(port);
    }
}

internal static class LocalChromeProcessCommandLine
{
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

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
            if (status != StatusInfoLengthMismatch && status != StatusBufferTooSmall)
            {
                return null;
            }

            if (length <= 0)
            {
                return null;
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

internal static class LocalChromeTcpTable
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int ErrorInsufficientBuffer = 122;

    public static int? FindPidListeningOnLocalPort(int port)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (result != 0 && result != ErrorInsufficientBuffer)
        {
            return null;
        }

        if (size <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (result != 0)
            {
                return null;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(rowPtr);
                if (Ntosts(row.LocalPort) == port && row.OwningPid > 0)
                {
                    return (int)row.OwningPid;
                }

                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return null;
    }

    private static int Ntosts(uint networkPort) =>
        (int)(((networkPort & 0xFF) << 8) | ((networkPort >> 8) & 0xFF));

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int tcpTableLength,
        bool order,
        int ipVersion,
        int tableClass,
        int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }
}
