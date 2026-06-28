using System.Diagnostics;
using System.Management;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class SystemMetricsCollector
{
    private PerformanceCounter? _cpuCounter;
    private long _totalRamMb = -1;

    public WorkerSystemMetricsDto Collect()
    {
        var cpu = ReadCpuPercent();
        var (usedMb, totalMb) = ReadRam();
        var ramPercent = totalMb > 0 ? usedMb * 100.0 / totalMb : 0;
        return new WorkerSystemMetricsDto(cpu, ramPercent, usedMb, totalMb);
    }

    private double ReadCpuPercent()
    {
        try
        {
            _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _cpuCounter.NextValue();
            Thread.Sleep(100);
            return Math.Round(_cpuCounter.NextValue(), 1);
        }
        catch
        {
            return 0;
        }
    }

    private (long UsedMb, long TotalMb) ReadRam()
    {
        try
        {
            if (_totalRamMb < 0)
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                foreach (var obj in searcher.Get().Cast<ManagementObject>())
                {
                    var totalKb = Convert.ToInt64(obj["TotalVisibleMemorySize"]);
                    var freeKb = Convert.ToInt64(obj["FreePhysicalMemory"]);
                    var totalMb = totalKb / 1024;
                    var usedMb = (totalKb - freeKb) / 1024;
                    _totalRamMb = totalMb;
                    return (usedMb, totalMb);
                }
            }

            using var freeSearcher = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var obj in freeSearcher.Get().Cast<ManagementObject>())
            {
                var freeKb = Convert.ToInt64(obj["FreePhysicalMemory"]);
                var usedMb = _totalRamMb - freeKb / 1024;
                return (usedMb, _totalRamMb);
            }
        }
        catch
        {
            // fallback
        }

        return (0, 0);
    }
}