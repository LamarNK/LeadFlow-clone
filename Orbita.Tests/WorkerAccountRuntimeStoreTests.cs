using System.Text.Json;
using LeadFlow.Core.Models;
using Orbita.Worker.Services;

namespace Orbita.Tests;

/// <summary>
/// Изоляция resume-файла через внутренний контрактор сторa (временный каталог):
/// файловый формат и конкурентная запись покрыты без касания реального
/// %LocalAppData%\OrbitaWorker.
/// </summary>
public sealed class WorkerAccountRuntimeStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "orbita-resume-tests-" + Guid.NewGuid().ToString("N"));

    private static AvitoAccount UnfinishedPassAccount(int phoneSpent, int repliesSpent, int restarts)
    {
        return new AvitoAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "store-test",
            MonitoringPassStartedAtUtc = DateTime.UtcNow.AddHours(-1),
            MonitoringPassFinishedAtUtc = null,
            MonitoringPassPhoneRevealClicksSpent = phoneSpent,
            MonitoringPassAutoRepliesSpent = repliesSpent,
            MonitoringPassSessionRestarts = restarts
        };
    }

    [Fact]
    public void ConcurrentUpserts_DifferentAccounts_AllSurviveInResumeFile()
    {
        // Регрессия (ревью): без сериализации Upsert параллельные аккаунты воркера
        // одновременно писали account-resume.json — частичный JSON или snapshot
        // без самой свежей записи другого аккаунта (потеря расхода бюджета).
        var accounts = Enumerable.Range(0, 8)
            .Select(_ => UnfinishedPassAccount(0, 0, 0))
            .ToList();

        var store = new WorkerAccountRuntimeStore(_directory);
        Parallel.ForEach(
            accounts,
            account =>
            {
                for (var i = 0; i < 10; i++)
                {
                    account.MonitoringPassPhoneRevealClicksSpent++;
                    account.MonitoringPassAutoRepliesSpent += 2;
                    store.Upsert(account);
                }
            });

        // Файл валиден и содержит ВСЕ аккаунты с финальными значениями.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "account-resume.json")));
        var entries = doc.RootElement.GetProperty("accounts").EnumerateObject().ToDictionary(x => x.Name, x => x.Value);
        Assert.Equal(accounts.Count, entries.Count);
        foreach (var account in accounts)
        {
            var entry = entries[account.Id.ToString("D")];
            Assert.Equal(10, entry.GetProperty("passPhoneRevealClicksSpent").GetInt32());
            Assert.Equal(20, entry.GetProperty("passAutoRepliesSpent").GetInt32());
        }
    }

    [Fact]
    public void ResumeFile_ReloadsBudgetCountersForUnfinishedPass()
    {
        // Crash-resume: расход незавершённого прохода переживает рестарт процесса.
        var account = UnfinishedPassAccount(phoneSpent: 12, repliesSpent: 3, restarts: 2);
        var first = new WorkerAccountRuntimeStore(_directory);
        first.Upsert(account);

        var reloaded = new WorkerAccountRuntimeStore(_directory);
        var target = new AvitoAccount { Id = account.Id };
        reloaded.OverlayRuntime(target);

        Assert.Equal(12, target.MonitoringPassPhoneRevealClicksSpent);
        Assert.Equal(3, target.MonitoringPassAutoRepliesSpent);
        Assert.Equal(2, target.MonitoringPassSessionRestarts);
        Assert.NotNull(target.MonitoringPassStartedAtUtc);
        Assert.Null(target.MonitoringPassFinishedAtUtc);
    }

    [Fact]
    public void ResumeFile_FinishedPassDoesNotApplyStaleCounters()
    {
        // После FinishPass сохранённый расход не должен попадать в аккаунт нового прохода.
        var account = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "store-test-finished",
            MonitoringPassStartedAtUtc = DateTime.UtcNow.AddHours(-2),
            MonitoringPassFinishedAtUtc = DateTime.UtcNow.AddHours(-1),
            MonitoringPassPhoneRevealClicksSpent = 40,
            MonitoringPassAutoRepliesSpent = 9,
            MonitoringPassSessionRestarts = 6
        };
        var first = new WorkerAccountRuntimeStore(_directory);
        first.Upsert(account);

        var reloaded = new WorkerAccountRuntimeStore(_directory);
        var target = new AvitoAccount
        {
            Id = account.Id,
            MonitoringPassStartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            MonitoringPassFinishedAtUtc = null,
            MonitoringPassPhoneRevealClicksSpent = 0,
            MonitoringPassAutoRepliesSpent = 0,
            MonitoringPassSessionRestarts = 0
        };
        reloaded.OverlayRuntime(target);

        // Диск хранит завершённый старый проход; целевой аккаунт начал НОВЫЙ проход —
        // ApplyTo не должен подтягивать устаревший расход (timestamps разные, но
        // Math.Max применялся бы, если бы диск хранил незавершённый проход).
        // Здесь проверяем обратное: свежий незавершённый проход на цели не получает
        // счётчики завершённого прохода с диска.
        Assert.Equal(0, target.MonitoringPassPhoneRevealClicksSpent);
        Assert.Equal(0, target.MonitoringPassAutoRepliesSpent);
        Assert.Equal(0, target.MonitoringPassSessionRestarts);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Временный каталог; оставшиеся файлы уберёт ОС.
        }
    }
}
