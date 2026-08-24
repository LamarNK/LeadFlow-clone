using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

internal static class WorkerRestartHelper
{
    public const string UpdateRestartArgument = "--update-restart";

    public static string BuildInstallBatchScript(
        string msiPath,
        string exePath,
        string workingDir,
        int workerPid,
        bool updateRestart = true,
        string? version = null,
        string? msiLogPath = null,
        string? statusPath = null,
        string? scriptPath = null)
    {
        var resolvedVersion = ResolveVersion(version, msiPath);
        var request = new WorkerUpdateBatchScript.Request(
            msiPath,
            exePath,
            workingDir,
            workerPid,
            resolvedVersion,
            msiLogPath ?? WorkerUpdateStore.BuildMsiLogPath(resolvedVersion),
            statusPath ?? WorkerUpdateStore.SelfUpdateStatusPath,
            scriptPath ?? string.Empty,
            DateTime.UtcNow.ToString("o"),
            updateRestart);
        return WorkerUpdateBatchScript.BuildInstall(request);
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
        script
            .AppendLine($"set \"WORKER_PID={workerPid}\"")
            .AppendLine("set WAIT_SEC=0")
            .AppendLine(":wait_worker")
            .AppendLine("tasklist /FI \"PID eq %WORKER_PID%\" 2>nul | find \"%WORKER_PID%\" >nul")
            .AppendLine("if %ERRORLEVEL%==0 (")
            .AppendLine("  set /a WAIT_SEC+=2")
            .AppendLine($"  if %WAIT_SEC% GEQ {WorkerUpdateBatchScript.MaxWaitWorkerExitSeconds} goto kill_worker")
            .AppendLine("  timeout /t 2 /nobreak >nul")
            .AppendLine("  goto wait_worker")
            .AppendLine(")")
            .AppendLine("goto restart")
            .AppendLine(":kill_worker")
            .AppendLine("taskkill /F /PID %WORKER_PID% >nul 2>&1")
            .AppendLine("timeout /t 3 /nobreak >nul")
            .AppendLine(":restart")
            .AppendLine($"timeout /t {WorkerUpdateBatchScript.RestartDelaySeconds} /nobreak >nul")
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
                $"/c timeout /t {WorkerUpdateBatchScript.RestartDelaySeconds} /nobreak >nul & start \"\" /D \"{EscapeCmdPath(workingDir)}\" \"{EscapeCmdPath(exePath)}\"{restartArg}";
            return TryStartCmd(arguments, null, workerPid, updateRestart, exePath, workingDir);
        }

        var scriptDir = WorkerUpdateStore.GetUpdateWorkingDirectory();
        Directory.CreateDirectory(scriptDir);
        var scriptPath = Path.Combine(scriptDir, $"restart-{Guid.NewGuid():N}.cmd");
        var script = BuildRestartBatchScript(
            exePath,
            workingDir,
            workerPid.Value,
            updateRestart);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return TryStartDetachedScript(scriptPath, scriptDir, workerPid, updateRestart, exePath, workingDir);
    }

    public static bool LaunchInstallScript(string msiPath, int? workerPid = null, bool updateRestart = true, string? version = null)
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

        var resolvedVersion = ResolveVersion(version, msiPath);
        var scriptDir = WorkerUpdateStore.GetUpdateWorkingDirectory();
        Directory.CreateDirectory(scriptDir);
        Directory.CreateDirectory(WorkerUpdateStore.UpdateLogsDirectory);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var scriptPath = Path.Combine(scriptDir, $"apply-{resolvedVersion}-{timestamp}.cmd");
        var logPath = WorkerUpdateStore.BuildMsiLogPath(resolvedVersion, timestamp);
        var statusPath = WorkerUpdateStore.SelfUpdateStatusPath;
        var script = WorkerUpdateBatchScript.BuildInstall(new WorkerUpdateBatchScript.Request(
            msiPath,
            exePath,
            workingDir,
            workerPid ?? Environment.ProcessId,
            resolvedVersion,
            logPath,
            statusPath,
            scriptPath,
            DateTime.UtcNow.ToString("o"),
            updateRestart));
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        WriteLaunchRecord(msiPath, exePath, workingDir, scriptPath, logPath, statusPath, resolvedVersion, workerPid);

        return TryStartDetachedScript(scriptPath, scriptDir, workerPid, updateRestart, exePath, workingDir);
    }

    public static bool TryDetectLegacyPerMachineInstall(out string? details)
    {
        details = null;
        var processPath = Environment.ProcessPath;
        if (IsProgramFilesPath(processPath))
        {
            details = processPath;
            return true;
        }

        try
        {
            foreach (var hivePath in new[]
                     {
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                     })
            {
                using var root = Registry.LocalMachine.OpenSubKey(hivePath);
                if (root is null)
                {
                    continue;
                }

                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    using var subKey = root.OpenSubKey(subKeyName);
                    if (subKey is null)
                    {
                        continue;
                    }

                    var displayName = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)
                        || displayName.IndexOf("Orbita Worker", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    // Per-user WiX ARP lives in HKCU. Any HKLM hit is a leftover per-machine product.
                    details = $"{displayName} ({subKeyName})";
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _ = WorkerLifecycleLog.WarningAsync(
                $"Worker update: не удалось проверить HKLM Uninstall: {ex.Message}",
                nameof(TryDetectLegacyPerMachineInstall));
        }

        return false;
    }

    public static string BuildManualElevationMessage(string msiPath, string? productDetails)
    {
        var product = string.IsNullOrWhiteSpace(productDetails) ? "Orbita Worker" : productDetails;
        return $"Тихая установка невозможна: найдена установка «{product}» для всех пользователей. Запустите MSI вручную от имени администратора один раз, не закрывая UAC. Файл: {msiPath}";
    }

    private static bool TryStartDetachedScript(
        string scriptPath,
        string scriptDir,
        int? workerPid,
        bool updateRestart,
        string exePath,
        string workingDir)
    {
        try
        {
            // `start` breaks the installer cmd away from this process so Environment.Exit
            // / job-object teardown cannot kill msiexec mid-upgrade.
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments =
                    $"/d /c start \"\" /min /D \"{scriptDir}\" cmd.exe /d /c call \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = scriptDir
            });

            if (process is null)
            {
                _ = WorkerLifecycleLog.ErrorAsync(
                    "Worker restart: Process.Start вернул null",
                    nameof(TryStartDetachedScript),
                    CreateRestartProperties(scriptPath, workerPid, updateRestart, exePath, workingDir));
                return false;
            }

            _ = WorkerLifecycleLog.InfoAsync(
                "Worker restart: запущен отвязанный cmd-скрипт",
                nameof(TryStartDetachedScript),
                CreateRestartProperties(scriptPath, workerPid, updateRestart, exePath, workingDir, process.Id));
            return true;
        }
        catch (Exception ex)
        {
            _ = WorkerLifecycleLog.ErrorAsync(
                $"Worker restart: ошибка запуска cmd: {ex.Message}",
                nameof(TryStartDetachedScript),
                CreateRestartProperties(scriptPath, workerPid, updateRestart, exePath, workingDir));
            return false;
        }
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

    private static void WriteLaunchRecord(
        string msiPath,
        string exePath,
        string workingDir,
        string scriptPath,
        string logPath,
        string statusPath,
        string version,
        int? workerPid)
    {
        try
        {
            Directory.CreateDirectory(WorkerUpdateStore.UpdateLogsDirectory);
            var recordPath = Path.Combine(WorkerUpdateStore.UpdateLogsDirectory, "last-launch.txt");
            var text =
                $"StartedAtUtc={DateTime.UtcNow:o}{Environment.NewLine}" +
                $"Version={version}{Environment.NewLine}" +
                $"WorkerPid={workerPid ?? Environment.ProcessId}{Environment.NewLine}" +
                $"MsiPath={msiPath}{Environment.NewLine}" +
                $"ExePath={exePath}{Environment.NewLine}" +
                $"WorkingDir={workingDir}{Environment.NewLine}" +
                $"ScriptPath={scriptPath}{Environment.NewLine}" +
                $"LogPath={logPath}{Environment.NewLine}" +
                $"StatusPath={statusPath}{Environment.NewLine}";
            File.WriteAllText(recordPath, text);
        }
        catch
        {
            // diagnostics must not block the update
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

    private static string ResolveVersion(string? version, string msiPath)
    {
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        return AppVersionHelper.TryParseVersionFromFileName(Path.GetFileName(msiPath), out var parsed)
            ? parsed
            : "unknown";
    }

    private static bool IsProgramFilesPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && (path.Contains(@"\Program Files\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\Program Files (x86)\", StringComparison.OrdinalIgnoreCase));

    private static string EscapeCmdPath(string path) => path.Replace("\"", "\"\"", StringComparison.Ordinal);
}
