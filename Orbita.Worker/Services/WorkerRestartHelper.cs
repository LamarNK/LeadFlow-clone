using System.Diagnostics;

namespace Orbita.Worker.Services;

internal static class WorkerRestartHelper
{
    public static void ScheduleRestart()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
        }

        Environment.Exit(0);
    }
}