using System.Text.Json;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class WorkerRowActivityMappingTests
{
    private static readonly DateTime Now = new(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid WorkerId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public void MapRow_WaitingActivity_PreservesPhaseAndNextCycleAtUtc()
    {
        var nextCycleAtUtc = Now.AddMinutes(32);
        var row = WorkersService.MapRow(new WorkerListItem(
            WorkerId,
            "Worker #1",
            "WIN-W01",
            "1.0.0",
            "waiting",
            null,
            true,
            true,
            Now.AddSeconds(-5),
            10,
            0,
            0,
            0,
            CurrentActivity: new WorkerActivityDto(
                WorkerActivityPhases.Waiting,
                "ожидание",
                null,
                null,
                null,
                null,
                nextCycleAtUtc,
                Now.AddSeconds(-5))));

        Assert.Equal("Пауза · осталось 32 мин", row.CurrentActivityLabel);
        Assert.Equal("muted", row.CurrentActivityTone);
        Assert.Equal(WorkerActivityPhases.Waiting, row.CurrentActivityPhase);
        Assert.Equal(nextCycleAtUtc, row.CurrentActivityNextCycleAtUtc);
    }

    [Fact]
    public void WorkersLiveSnapshotJson_IncludesPhaseAndNextCycleAtUtc()
    {
        var nextCycleAtUtc = new DateTime(2026, 7, 2, 12, 32, 0, DateTimeKind.Utc);
        var snapshot = new WorkersLiveSnapshotViewModel
        {
            UpdatedAtUtc = Now,
            Workers =
            [
                new WorkerRowViewModel
                {
                    Id = WorkerId,
                    DisplayName = "Worker #1",
                    CurrentActivityLabel = "Пауза · осталось 32 мин",
                    CurrentActivityTone = "muted",
                    CurrentActivityPhase = WorkerActivityPhases.Waiting,
                    CurrentActivityNextCycleAtUtc = nextCycleAtUtc
                }
            ]
        };

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var worker = doc.RootElement.GetProperty("workers")[0];

        Assert.Equal("waiting", worker.GetProperty("currentActivityPhase").GetString());
        Assert.Equal(nextCycleAtUtc, worker.GetProperty("currentActivityNextCycleAtUtc").GetDateTime());
    }
}
