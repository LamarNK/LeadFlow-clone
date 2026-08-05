using Orbita.Logging.Audit;

namespace Orbita.Tests;

public sealed class GlobalLoggerTests
{
    [Theory]
    [InlineData(DeskLinkAuditLogLevel.Debug, true)]
    [InlineData(DeskLinkAuditLogLevel.Info, true)]
    [InlineData(DeskLinkAuditLogLevel.Warning, true)]
    [InlineData(DeskLinkAuditLogLevel.Error, true)]
    [InlineData(DeskLinkAuditLogLevel.Debug, false)]
    [InlineData(DeskLinkAuditLogLevel.Info, false)]
    [InlineData(DeskLinkAuditLogLevel.Warning, false)]
    [InlineData(DeskLinkAuditLogLevel.Error, false)]
    public async Task LogAsync_SuppressesRepeatedMessagesAtEveryLevel(DeskLinkAuditLogLevel level, bool useErrorKey)
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), "orbita-logger-tests", Guid.NewGuid().ToString("N"));
        var logger = new Logger(logDirectory);
        const string message = "Worker auth failed: worker disabled.";

        try
        {
            for (var i = 0; i < 8; i++)
            {
                await logger.LogAsync(
                    message,
                    level,
                    memberName: "LogAuthFailureAsync",
                    filePath: "WorkerApiKeyAuthenticationHandler.cs",
                    errorKey: useErrorKey ? $"test.repeated.{level}" : null);
            }

            var entries = await WaitForEntriesAsync(logger, message, expectedCount: 4);

            Assert.Equal(4, entries.Count);
            Assert.Equal(3, entries.Count(entry => entry.Message == message));
            Assert.Contains(entries, entry => entry.Message == $"{message} (suppressed 1 duplicates)");
        }
        finally
        {
            if (Directory.Exists(logDirectory))
                Directory.Delete(logDirectory, recursive: true);
        }
    }

    private static async Task<IReadOnlyList<LogFileEntry>> WaitForEntriesAsync(
        Logger logger,
        string message,
        int expectedCount)
    {
        IReadOnlyList<LogFileEntry> entries = [];
        for (var attempt = 0; attempt < 100; attempt++)
        {
            entries = (await logger.GetAllLogsAsync())
                .Where(entry => entry.Message.StartsWith(message, StringComparison.Ordinal))
                .ToList();
            if (entries.Count >= expectedCount)
            {
                await Task.Delay(100);
                return (await logger.GetAllLogsAsync())
                    .Where(entry => entry.Message.StartsWith(message, StringComparison.Ordinal))
                    .ToList();
            }

            await Task.Delay(10);
        }

        return entries;
    }
}
