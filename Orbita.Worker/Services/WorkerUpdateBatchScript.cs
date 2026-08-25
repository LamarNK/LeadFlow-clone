using System.Globalization;
using System.Text;

namespace Orbita.Worker.Services;

internal static class WorkerUpdateBatchScript
{
    public const int RestartDelaySeconds = 8;
    public const int MaxWaitWorkerExitSeconds = 120;

    public static readonly int[] SuccessExitCodes = [0, 1641, 3010];

    public sealed record Request(
        string MsiPath,
        string ExePath,
        string WorkingDir,
        int WorkerPid,
        string Version,
        string MsiLogPath,
        string StatusPath,
        string ScriptPath,
        string StartedAtUtc,
        bool UpdateRestart = true);

    public sealed record Status(
        string Version,
        int ExitCode,
        bool Success,
        string MsiPath,
        string LogPath,
        string ExePath,
        int WorkerPid,
        string ScriptPath,
        string? StartedAtUtc,
        string? Message);

    public static string BuildInstall(Request request)
    {
        var restartArg = request.UpdateRestart ? " --update-restart" : string.Empty;
        var script = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("setlocal EnableExtensions")
            .AppendLine("chcp 65001 >nul")
            .AppendLine($"set \"WORKER_PID={request.WorkerPid}\"")
            .AppendLine($"set \"MSI_PATH={EscapeCmd(request.MsiPath)}\"")
            .AppendLine($"set \"EXE_PATH={EscapeCmd(request.ExePath)}\"")
            .AppendLine($"set \"WORK_DIR={EscapeCmd(request.WorkingDir)}\"")
            .AppendLine($"set \"LOG_PATH={EscapeCmd(request.MsiLogPath)}\"")
            .AppendLine($"set \"STATUS_PATH={EscapeCmd(request.StatusPath)}\"")
            .AppendLine($"set \"SCRIPT_PATH={EscapeCmd(request.ScriptPath)}\"")
            .AppendLine($"set \"TARGET_VERSION={EscapeCmd(request.Version)}\"")
            .AppendLine($"set \"STARTED_AT={EscapeCmd(request.StartedAtUtc)}\"")
            .AppendLine($"set \"RESTART_ARG={restartArg}\"");
        AppendWaitForWorkerExit(script);
        script
            .AppendLine(":install")
            .AppendLine("\"%SystemRoot%\\System32\\msiexec.exe\" /qn /norestart REINSTALLMODE=amus /i \"%MSI_PATH%\" /l*v \"%LOG_PATH%\"")
            .AppendLine("set \"INSTALL_EXIT=%ERRORLEVEL%\"")
            .AppendLine("set \"SUCCESS=0\"")
            .AppendLine("if \"%INSTALL_EXIT%\"==\"0\" set \"SUCCESS=1\"")
            .AppendLine("if \"%INSTALL_EXIT%\"==\"1641\" set \"SUCCESS=1\"")
            .AppendLine("if \"%INSTALL_EXIT%\"==\"3010\" set \"SUCCESS=1\"")
            .AppendLine("if \"%SUCCESS%\"==\"1\" goto install_ok")
            .AppendLine("call :write_status")
            .AppendLine("exit /b %INSTALL_EXIT%")
            .AppendLine()
            .AppendLine(":install_ok")
            .AppendLine("if exist \"%EXE_PATH%\" goto restart_worker")
            .AppendLine("set \"SUCCESS=0\"")
            .AppendLine("set \"INSTALL_EXIT=3\"")
            .AppendLine("call :write_status")
            .AppendLine("exit /b 3")
            .AppendLine()
            .AppendLine(":restart_worker")
            .AppendLine($"timeout /t {RestartDelaySeconds} /nobreak >nul")
            .AppendLine("tasklist /FI \"IMAGENAME eq Orbita.Worker.exe\" 2>nul | find /I \"Orbita.Worker.exe\" >nul")
            .AppendLine("if not errorlevel 1 goto cleanup")
            .AppendLine("start \"\" /D \"%WORK_DIR%\" \"%EXE_PATH%\"%RESTART_ARG%")
            .AppendLine()
            .AppendLine(":cleanup")
            .AppendLine("call :write_status")
            .AppendLine("del /q \"%MSI_PATH%\" 2>nul")
            .AppendLine("del /q \"%~f0\" 2>nul")
            .AppendLine("exit /b 0")
            .AppendLine()
            .AppendLine(":write_status")
            .AppendLine("> \"%STATUS_PATH%\" (")
            .AppendLine("  echo Version=%TARGET_VERSION%")
            .AppendLine("  echo ExitCode=%INSTALL_EXIT%")
            .AppendLine("  echo Success=%SUCCESS%")
            .AppendLine("  echo MsiPath=%MSI_PATH%")
            .AppendLine("  echo LogPath=%LOG_PATH%")
            .AppendLine("  echo ExePath=%EXE_PATH%")
            .AppendLine("  echo WorkerPid=%WORKER_PID%")
            .AppendLine("  echo ScriptPath=%SCRIPT_PATH%")
            .AppendLine("  echo StartedAtUtc=%STARTED_AT%")
            .AppendLine(")")
            .AppendLine("goto :eof")
            .AppendLine();
        return script.ToString();
    }

    public static Status? ParseStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[line[..separator]] = line[(separator + 1)..];
        }

        if (!values.TryGetValue("Version", out var version) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var exitCode = 1;
        if (values.TryGetValue("ExitCode", out var exitText))
        {
            _ = int.TryParse(exitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out exitCode);
        }

        var success = values.TryGetValue("Success", out var successText)
            && (successText == "1" || successText.Equals("true", StringComparison.OrdinalIgnoreCase));

        values.TryGetValue("MsiPath", out var msiPath);
        values.TryGetValue("LogPath", out var logPath);
        values.TryGetValue("ExePath", out var exePath);
        values.TryGetValue("ScriptPath", out var scriptPath);
        values.TryGetValue("StartedAtUtc", out var startedAt);
        var workerPid = 0;
        if (values.TryGetValue("WorkerPid", out var pidText))
        {
            _ = int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out workerPid);
        }

        return new Status(
            version.Trim(),
            exitCode,
            success,
            msiPath ?? string.Empty,
            logPath ?? string.Empty,
            exePath ?? string.Empty,
            workerPid,
            scriptPath ?? string.Empty,
            startedAt,
            FormatResultMessage(exitCode, success, logPath, exePath, msiPath));
    }

    public static string FormatResultMessage(
        int exitCode,
        bool success,
        string? logPath,
        string? exePath,
        string? msiPath = null)
    {
        var suffix = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(msiPath))
        {
            suffix.Append(" MSI: ").Append(msiPath);
        }

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            suffix.Append(" Журнал MSI: ").Append(logPath);
        }

        if (success)
        {
            return "Обновление установлено." + suffix;
        }

        if (exitCode == 3)
        {
            return $"MSI вернул успех, но файл воркера не найден ({exePath})." + suffix;
        }

        var body = exitCode switch
        {
            1730 or 1603 =>
                $"Установка MSI не удалась (код {exitCode}). Если воркер раньше ставился для всех пользователей или от администратора, запустите скачанный MSI вручную от имени администратора один раз.",
            1618 =>
                $"Установка MSI не удалась: уже выполняется другая установка Windows Installer (код 1618).",
            1619 =>
                $"MSI-пакет не удалось открыть (код 1619).",
            1638 =>
                $"Другая версия продукта уже установлена (код 1638).",
            1612 =>
                $"Не найден исходный пакет предыдущей установки (код 1612).",
            1625 =>
                $"Установка MSI отклонена политикой (код 1625).",
            5 =>
                $"Установка MSI не удалась: отказано в доступе (код 5).",
            _ =>
                $"Установка MSI не удалась (код {exitCode})."
        };

        return body + suffix;
    }

    public static bool IsSuccessExitCode(int exitCode) =>
        Array.IndexOf(SuccessExitCodes, exitCode) >= 0;

    public static bool ShouldBlockSilentRetry(int exitCode) =>
        exitCode is 3 or 5 or 1603 or 1612 or 1625 or 1638 or 1730;

    private static void AppendWaitForWorkerExit(StringBuilder script)
    {
        script
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
            .AppendLine("timeout /t 3 /nobreak >nul");
    }

    private static string EscapeCmd(string path) => path.Replace("\"", "\"\"", StringComparison.Ordinal);
}
