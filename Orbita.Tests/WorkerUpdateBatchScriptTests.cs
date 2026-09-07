using Orbita.Worker.Services;

namespace Orbita.Tests;

public sealed class WorkerUpdateBatchScriptTests
{
    private static WorkerUpdateBatchScript.Request CreateRequest() =>
        new(
            MsiPath: @"C:\Temp\orbita-worker-update\Orbita.Worker.Setup-1.2.3.4.msi",
            ExePath: @"C:\Users\test\AppData\Local\Orbita\Worker\Orbita.Worker.exe",
            WorkingDir: @"C:\Users\test\AppData\Local\Orbita\Worker",
            WorkerPid: 4242,
            Version: "1.2.3.4",
            MsiLogPath: @"C:\Users\test\AppData\Local\OrbitaWorker\update-logs\msi-1.2.3.4-20260824T120000Z.log",
            StatusPath: @"C:\Users\test\AppData\Local\OrbitaWorker\self-update-status.txt",
            ScriptPath: @"C:\Temp\orbita-worker-update\apply-1.2.3.4.cmd",
            StartedAtUtc: "2026-08-24T12:00:00.0000000Z");

    [Fact]
    public void BuildInstall_WritesVerboseMsiLogAndCapturesExitCode()
    {
        var script = WorkerUpdateBatchScript.BuildInstall(CreateRequest());

        Assert.Contains("chcp 65001", script, StringComparison.Ordinal);
        Assert.Contains("/l*v \"%LOG_PATH%\"", script, StringComparison.Ordinal);
        Assert.Contains("%SystemRoot%\\System32\\msiexec.exe", script, StringComparison.Ordinal);
        Assert.Contains("/qn /norestart REINSTALLMODE=amus /i", script, StringComparison.Ordinal);
        Assert.Contains("set \"INSTALL_EXIT=%ERRORLEVEL%\"", script, StringComparison.Ordinal);
        Assert.Contains(@"set ""LOG_PATH=C:\Users\test\AppData\Local\OrbitaWorker\update-logs\msi-1.2.3.4-20260824T120000Z.log""", script, StringComparison.Ordinal);
        Assert.Contains("echo ExitCode=%INSTALL_EXIT%", script, StringComparison.Ordinal);
        Assert.Contains("echo ScriptPath=%SCRIPT_PATH%", script, StringComparison.Ordinal);
        Assert.Contains("echo WorkerPid=%WORKER_PID%", script, StringComparison.Ordinal);
        Assert.Contains("echo MsiPath=%MSI_PATH%", script, StringComparison.Ordinal);
        Assert.Contains("echo ExePath=%EXE_PATH%", script, StringComparison.Ordinal);
        Assert.Contains("echo LogPath=%LOG_PATH%", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_SuccessfulPath_ChecksExeThenRestartsAndDeletesArtifacts()
    {
        var script = WorkerUpdateBatchScript.BuildInstall(CreateRequest());

        Assert.Contains("if \"%INSTALL_EXIT%\"==\"0\" set \"SUCCESS=1\"", script, StringComparison.Ordinal);
        Assert.Contains("if \"%INSTALL_EXIT%\"==\"1641\" set \"SUCCESS=1\"", script, StringComparison.Ordinal);
        Assert.Contains("if \"%INSTALL_EXIT%\"==\"3010\" set \"SUCCESS=1\"", script, StringComparison.Ordinal);
        Assert.Contains("if exist \"%EXE_PATH%\" goto restart_worker", script, StringComparison.Ordinal);
        Assert.Contains("start \"\" /D \"%WORK_DIR%\" \"%EXE_PATH%\"%RESTART_ARG%", script, StringComparison.Ordinal);
        Assert.Contains("--update-restart", script, StringComparison.Ordinal);
        Assert.Contains("del /q \"%MSI_PATH%\" 2>nul", script, StringComparison.Ordinal);
        Assert.Contains("del /q \"%~f0\" 2>nul", script, StringComparison.Ordinal);

        var cleanup = script.IndexOf(":cleanup", StringComparison.Ordinal);
        var delMsi = script.IndexOf("del /q \"%MSI_PATH%\"", StringComparison.Ordinal);
        var delSelf = script.IndexOf("del /q \"%~f0\"", StringComparison.Ordinal);
        var startExe = script.IndexOf("start \"\" /D \"%WORK_DIR%\"", StringComparison.Ordinal);
        Assert.True(cleanup >= 0 && delMsi > cleanup && delSelf > cleanup);
        Assert.True(startExe > script.IndexOf(":restart_worker", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildInstall_FailedMsi_RestartsExistingExeAndKeepsPackage()
    {
        var script = WorkerUpdateBatchScript.BuildInstall(CreateRequest());

        var installOk = script.IndexOf(":install_ok", StringComparison.Ordinal);
        var recover = script.IndexOf(":recover_worker", StringComparison.Ordinal);
        var cleanup = script.IndexOf(":cleanup", StringComparison.Ordinal);
        Assert.True(installOk >= 0 && recover > installOk && cleanup >= 0);

        var failureBranch = script[..installOk];
        Assert.Contains("goto recover_worker", failureBranch, StringComparison.Ordinal);
        Assert.Contains("call :write_status", failureBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("del /q \"%MSI_PATH%\"", failureBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("del /q \"%~f0\"", failureBranch, StringComparison.Ordinal);

        var recoverBlock = script[recover..];
        Assert.Contains("if not exist \"%EXE_PATH%\"", recoverBlock, StringComparison.Ordinal);
        Assert.Contains("call :start_worker_if_needed", recoverBlock, StringComparison.Ordinal);
        Assert.Contains("exit /b %INSTALL_EXIT%", recoverBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("del /q \"%MSI_PATH%\"", recoverBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("del /q \"%~f0\"", recoverBlock, StringComparison.Ordinal);

        var delMsi = script.IndexOf("del /q \"%MSI_PATH%\"", StringComparison.Ordinal);
        Assert.True(delMsi > cleanup);
    }

    [Fact]
    public void ParseStatus_PersistsExitCodeAndFailureMessage()
    {
        var text = """
            Version=1.2.3.4
            ExitCode=1603
            Success=0
            MsiPath=C:\Temp\setup.msi
            LogPath=C:\Logs\msi.log
            ExePath=C:\Worker\Orbita.Worker.exe
            WorkerPid=4242
            ScriptPath=C:\Temp\apply.cmd
            StartedAtUtc=2026-08-24T12:00:00Z
            """;

        var status = WorkerUpdateBatchScript.ParseStatus(text);

        Assert.NotNull(status);
        Assert.Equal("1.2.3.4", status.Version);
        Assert.Equal(1603, status.ExitCode);
        Assert.False(status.Success);
        Assert.Equal(@"C:\Temp\setup.msi", status.MsiPath);
        Assert.Equal(@"C:\Logs\msi.log", status.LogPath);
        Assert.Contains("1603", status.Message, StringComparison.Ordinal);
        Assert.Contains(@"C:\Logs\msi.log", status.Message, StringComparison.Ordinal);
        Assert.Contains(@"C:\Temp\setup.msi", status.Message, StringComparison.Ordinal);
        Assert.Contains("администратора", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseStatus_Success_DoesNotWarnAboutElevation()
    {
        var status = WorkerUpdateBatchScript.ParseStatus("""
            Version=1.2.3.4
            ExitCode=0
            Success=1
            LogPath=C:\Logs\msi.log
            """);

        Assert.NotNull(status);
        Assert.True(status.Success);
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("установлено", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("администратора", status.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1641, true)]
    [InlineData(3010, true)]
    [InlineData(1603, false)]
    [InlineData(1618, false)]
    [InlineData(1730, false)]
    public void IsSuccessExitCode_RecognizesMsiSuccessValues(int exitCode, bool expected) =>
        Assert.Equal(expected, WorkerUpdateBatchScript.IsSuccessExitCode(exitCode));

    [Theory]
    [InlineData(1603, true)]
    [InlineData(1730, true)]
    [InlineData(1638, true)]
    [InlineData(1612, true)]
    [InlineData(1618, false)]
    [InlineData(1619, false)]
    [InlineData(1620, false)]
    [InlineData(0, false)]
    public void ShouldBlockSilentRetry_OnlyForElevationOrFatalCodes(int exitCode, bool expected) =>
        Assert.Equal(expected, WorkerUpdateBatchScript.ShouldBlockSilentRetry(exitCode));

    [Theory]
    [InlineData(1619, true)]
    [InlineData(1620, true)]
    [InlineData(1603, false)]
    [InlineData(1618, false)]
    [InlineData(0, false)]
    public void IsCorruptPackageExitCode_DetectsUnreadableMsi(int exitCode, bool expected) =>
        Assert.Equal(expected, WorkerUpdateBatchScript.IsCorruptPackageExitCode(exitCode));

    [Theory]
    [InlineData(1603, true)]
    [InlineData(1618, true)]
    [InlineData(1619, false)]
    [InlineData(1620, false)]
    [InlineData(0, false)]
    public void ShouldRequeueDownloadedMsi_SkipsCorruptPackages(int exitCode, bool expected) =>
        Assert.Equal(expected, WorkerUpdateBatchScript.ShouldRequeueDownloadedMsi(exitCode));

    [Fact]
    public void FormatResultMessage_DoesNotHide1618Or1603()
    {
        var busy = WorkerUpdateBatchScript.FormatResultMessage(1618, false, @"C:\log.txt", null, @"C:\setup.msi");
        var failed = WorkerUpdateBatchScript.FormatResultMessage(1603, false, @"C:\log.txt", null, @"C:\setup.msi");
        Assert.Contains("1618", busy, StringComparison.Ordinal);
        Assert.Contains("1603", failed, StringComparison.Ordinal);
        Assert.Contains(@"C:\log.txt", failed, StringComparison.Ordinal);
        Assert.Contains(@"C:\setup.msi", failed, StringComparison.Ordinal);
        Assert.Contains("1730", WorkerUpdateBatchScript.FormatResultMessage(1730, false, @"C:\log.txt", null), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatResultMessage_NamesCorruptOrIncompletePackage()
    {
        var message = WorkerUpdateBatchScript.FormatResultMessage(1620, false, @"C:\log.txt", null, @"C:\setup.msi");
        Assert.Contains("1620", message, StringComparison.Ordinal);
        Assert.Contains("поврежд", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("докачал", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\setup.msi", message, StringComparison.Ordinal);
    }
}
