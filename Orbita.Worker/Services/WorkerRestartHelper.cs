using System.Diagnostics;
using System.Text;

namespace Orbita.Worker.Services;

internal static class WorkerRestartHelper
{
    private const int RestartDelaySeconds = 10;
    public const string UpdateRestartArgument = "--update-restart";

    public static void LaunchProcessRestart(bool updateRestart = false)
    {
        var exePath = Environment.ProcessPath;
        var workingDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        var restartArg = updateRestart ? $" {UpdateRestartArgument}" : string.Empty;
        var arguments =
            $"/c timeout /t {RestartDelaySeconds} /nobreak >nul & start \"\" /D \"{EscapeCmdPath(workingDir)}\" \"{EscapeCmdPath(exePath)}\"{restartArg}";
        Process.Start(new ProcessStartInfo("cmd.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    public static void LaunchInstallScript(string msiPath, bool updateRestart = true)
    {
        var exePath = Environment.ProcessPath;
        var workingDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        var scriptDir = Path.Combine(Path.GetTempPath(), "orbita-worker-update");
        Directory.CreateDirectory(scriptDir);
        var scriptPath = Path.Combine(scriptDir, $"apply-{Guid.NewGuid():N}.cmd");
        var restartArg = updateRestart ? $" {UpdateRestartArgument}" : string.Empty;
        var script = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine($"msiexec /qn /i \"{EscapeCmdPath(msiPath)}\"")
            .AppendLine("set INSTALL_EXIT=%ERRORLEVEL%")
            .AppendLine($"del /q \"{EscapeCmdPath(msiPath)}\" 2>nul")
            .AppendLine($"timeout /t {RestartDelaySeconds} /nobreak >nul")
            .AppendLine($"start \"\" /D \"{EscapeCmdPath(workingDir)}\" \"{EscapeCmdPath(exePath)}\"{restartArg}")
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
    }

    private static string EscapeCmdPath(string path) => path.Replace("\"", "\"\"");
}