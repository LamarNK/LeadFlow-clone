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
        var script = new StringBuilder()
            .AppendLine("@echo off");
        AppendWaitForWorkerExit(script, workerPid, "install");
        script
            .AppendLine(":install")
            .AppendLine($"msiexec /qn /i \"{EscapeCmdPath(msiPath)}\"")
            .AppendLine("set INSTALL_EXIT=%ERRORLEVEL%")
            .AppendLine($"del /q \"{EscapeCmdPath(msiPath)}\" 2>nul")
            .AppendLine($"timeout /t {RestartDelaySeconds} /nobreak >nul")
            .AppendLine($"start \"\" /D \"{EscapeCmdPath(workingDir)}\" \"{EscapeCmdPath(exePath)}\"{restartArg}")
            .AppendLine("del /q \"%~f0\" 2>nul")
            .AppendLine("exit /b %INSTALL_EXIT%");
        return script.ToString();
    }

    public static string BuildRestartBatchScript(
        string exePath,
        string workingDir,
        int workerPid,
        bool updateRestart = true)
    {
        var restartArg = updateRestart ? $" {UpdateRestartArgument}" : string.Empty;
        var script = new StringBuilder()
            .AppendLine("@echo off");
        AppendWaitForWorkerExit(script, workerPid, "restart");
        script
            .AppendLine(":restart")
            .AppendLine($"timeout /t {RestartDelaySeconds} /nobreak >nul")
            .AppendLine($"start \"\" /D \"{EscapeCmdPath(workingDir)}\" \"{EscapeCmdPath(exePath)}\"{restartArg}")
            .AppendLine("del /q \"%~f0\" 2>nul")
            .AppendLine("exit /b 0");
        return script.ToString();
    }

    public static bool LaunchProcessRestart(int? workerPid = null, bool updateRestart = true)
    {
        var exePath = Environment.ProcessPath;
        var workingDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _ = WorkerLifecycleLog.ErrorAsync(
                "Worker restart: не удалось запустить — Environment.ProcessPath пуст",
                nameof(LaunchProcessRestart));
            return false;
        }

        if (workerPid is null)
        {
            var restartArg = updateRestart ? $" {UpdateRestartArgument}" : string.Empty;
            var arguments =
                $"/c timeout /t {RestartDelaySeconds} /nobreak >nul & start \"\" /D \"{EscapeCmdPath(workingDir)}\" \"{EscapeCmdPath(exePath)}\"{restartArg}";
            return TryStartCmd(arguments, null, workerPid, updateRestart, exePath, workingDir);
        }

        var scriptDir = Path.Combine(Path.GetTempPath(), "orbita-worker-update");
        Directory.CreateDirectory(scriptDir);
        var scriptPath = Path.Combine(scriptDir, $"restart-{Guid.NewGuid():N}.cmd");
        var script = BuildRestartBatchScript(
            exePath,
            workingDir,
            workerPid.Value,
            updateRestart);
        File.WriteAllText(scriptPath, script, Encoding.UTF8);

        return TryStartCmd($"/c \"{scriptPath}\"", scriptPath, workerPid, updateRestart, exePath, workingDir, scriptDir);
    }

    public static bool LaunchInstallScript(string msiPath, int? workerPid = null, bool updateRestart = true)
    {
        var exePath = Environment.ProcessPath;
        var workingDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _ = WorkerLifecycleLog.ErrorAsync(
                "Worker update: не удалось запустить установщик — Environment.ProcessPath пуст",
                nameof(LaunchInstallScript),
                new Dictionary<string, object?> { ["update.msiPath"] = msiPath });
            return false;
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

        return TryStartCmd($"/c \"{scriptPath}\"", scriptPath, workerPid, updateRestart, exePath, workingDir, scriptDir);
    }

    private static bool TryStartCmd(
        string arguments,
        string? scriptPath,
        int? workerPid,
        bool updateRestart,
        string exePath,
        string workingDir,
        string? scriptDir = null)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = scriptDir ?? AppContext.BaseDirectory
            });

            if (process is null)
            {
                _ = WorkerLifecycleLog.ErrorAsync(
                    "Worker restart: Process.Start вернул null",
                    nameof(TryStartCmd),
                    CreateRestartProperties(scriptPath, workerPid, updateRestart, exePath, workingDir));
                return false;
            }

            _ = WorkerLifecycleLog.InfoAsync(
                scriptPath is null
                    ? "Worker restart: запущен отложенный перезапуск без скрипта"
                    : "Worker restart: запущен фоновый cmd-скрипт",
                nameof(TryStartCmd),
                CreateRestartProperties(scriptPath, workerPid, updateRestart, exePath, workingDir, process.Id));

            return true;
        }
        catch (Exception ex)
        {
            _ = WorkerLifecycleLog.ErrorAsync(
                $"Worker restart: ошибка запуска cmd: {ex.Message}",
                nameof(TryStartCmd),
                CreateRestartProperties(scriptPath, workerPid, updateRestart, exePath, workingDir));
            return false;
        }
    }

    private static Dictionary<string, object?> CreateRestartProperties(
        string? scriptPath,
        int? workerPid,
        bool updateRestart,
        string exePath,
        string workingDir,
        int? cmdPid = null)
    {
        var properties = new Dictionary<string, object?>
        {
            ["restart.exePath"] = exePath,
            ["restart.workingDir"] = workingDir,
            ["restart.updateRestart"] = updateRestart,
            ["restart.waitPid"] = workerPid ?? Environment.ProcessId
        };

        if (!string.IsNullOrWhiteSpace(scriptPath))
        {
            properties["restart.scriptPath"] = scriptPath;
        }

        if (cmdPid is not null)
        {
            properties["restart.cmdPid"] = cmdPid.Value;
        }

        return properties;
    }

    private static void AppendWaitForWorkerExit(StringBuilder script, int workerPid, string continueLabel)
    {
        script
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
            .AppendLine($"goto {continueLabel}")
            .AppendLine(":kill_worker")
            .AppendLine("taskkill /F /PID %WORKER_PID% >nul 2>&1")
            .AppendLine("timeout /t 3 /nobreak >nul");
    }

    private static string EscapeCmdPath(string path) => path.Replace("\"", "\"\"");
}