using System.Diagnostics;
using System.Text;

namespace Orbita.Worker.Services;

internal static class WorkerRestartHelper
{
    private const int RestartDelaySeconds = 10;
    private const int MaxWaitWorkerExitSeconds = 120;
    public const string UpdateRestartArgument = "--update-restart";

    public static string BuildInstallBatchScript(
        string msiPath,
        string exePath,
        string workingDir,
        int workerPid,
        bool updateRestart = true)
    {
        var restartArg = updateRestart ? $" {UpdateRestartArgument}" : string.Empty;
        return new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine($"set WORKER_PID={workerPid}")
            .AppendLine("set WAIT_SEC=0")
            .AppendLine(":wait_worker")
            .AppendLine("tasklist /FI \"PID eq %WORKER_PID%\" 2>nul | find \"%WORKER_PID%\" >nul")
            .AppendLine("if %ERRORLEVEL%==0 (")
            .AppendLine("  set /a WAIT_SEC+=2")
            .AppendLine($"  if %WAIT_SEC% GEQ {MaxWaitWorkerExitSeconds} goto kill_worker")
            .AppendLine("  timeout /t 2 /nobreak >nul")
            .AppendLine("  goto wait_worker")
            .AppendLine(")")
            .AppendLine("goto install")
            .AppendLine(":kill_worker")
            .AppendLine("taskkill /F /PID %WORKER_PID% >nul 2>&1")
            .AppendLine("timeout /t 3 /nobreak >nul")
            .AppendLine(":install")
            .AppendLine($"msiexec /qn /i \"{EscapeCmdPath(msiPath)}\"")
            .AppendLine("set INSTALL_EXIT=%ERRORLEVEL%")
            .AppendLine($"del /q \"{EscapeCmdPath(msiPath)}\" 2>nul")
            .AppendLine($"timeout /t {RestartDelaySeconds} /nobreak >nul")
            .AppendLine($"start \"\" /D \"{EscapeCmdPath(workingDir)}\" \"{EscapeCmdPath(exePath)}\"{restartArg}")
            .AppendLine("del /q \"%~f0\" 2>nul")
            .AppendLine("exit /b %INSTALL_EXIT%")
            .ToString();
    }

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

    public static void LaunchInstallScript(string msiPath, int? workerPid = null, bool updateRestart = true)
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
        var script = BuildInstallBatchScript(
            msiPath,
            exePath,
            workingDir,
            workerPid ?? Environment.ProcessId,
            updateRestart);
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