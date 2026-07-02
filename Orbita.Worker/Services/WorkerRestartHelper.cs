using System.Diagnostics;
using System.Text;

namespace Orbita.Worker.Services;

internal static class WorkerRestartHelper
{
    private const int RestartDelaySeconds = 2;

    public static void ScheduleRestart()
    {
        ScheduleProcessStart(Environment.ProcessPath, AppContext.BaseDirectory);
        Environment.Exit(0);
    }

    public static void ScheduleInstallAndRestart(string msiPath)
    {
        var exePath = Environment.ProcessPath;
        var workingDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            Environment.Exit(0);
            return;
        }

        var scriptDir = Path.Combine(Path.GetTempPath(), "orbita-worker-update");
        Directory.CreateDirectory(scriptDir);
        var scriptPath = Path.Combine(scriptDir, $"apply-{Guid.NewGuid():N}.cmd");
        var script = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine($"msiexec /qn /i \"{EscapeCmdPath(msiPath)}\"")
            .AppendLine("set INSTALL_EXIT=%ERRORLEVEL%")
            .AppendLine($"del /q \"{EscapeCmdPath(msiPath)}\" 2>nul")
            .AppendLine($"timeout /t {RestartDelaySeconds} /nobreak >nul")
            .AppendLine($"start \"\" /D \"{EscapeCmdPath(workingDir)}\" \"{EscapeCmdPath(exePath)}\"")
            .AppendLine("del /q \"%~f0\" 2>nul")
            .AppendLine("exit /b %INSTALL_EXIT%")
            .ToString();
        File.WriteAllText(scriptPath, script, Encoding.UTF8);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = scriptDir
        });

        Environment.Exit(0);
    }

    private static void ScheduleProcessStart(string? exePath, string workingDir)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        var arguments =
            $"/c timeout /t {RestartDelaySeconds} /nobreak >nul & start \"\" /D \"{EscapeCmdPath(workingDir)}\" \"{EscapeCmdPath(exePath)}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static string EscapeCmdPath(string path) => path.Replace("\"", "\"\"");
}