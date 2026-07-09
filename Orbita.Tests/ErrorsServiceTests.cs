using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class ErrorsServiceTests
{
    private static readonly Guid WorkerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime OccurredAt = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_IncludesOnlyErrorLevelRows()
    {
        var items = new[]
        {
            CreateEvent("Warning", "капча / блок IP"),
            CreateEvent("Error", "Ошибка аккаунта Avito 1: disk space"),
            CreateEvent("Error", "Ошибка аккаунта Avito 2: timeout")
        };
        var rows = items
            .Where(e => e.Level is "Error")
            .Select(e => ErrorsIndexBuilder.MapEvent(e))
            .ToList();

        var model = ErrorsIndexBuilder.Build(rows, new(), page: 1);

        Assert.Equal(2, model.Pagination.TotalItems);
        Assert.All(model.Errors, e => Assert.DoesNotContain("капча", e.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EventsFilter_WarningsView_ExcludesErrors()
    {
        var rows = new[]
        {
            EventsIndexBuilder.MapEvent(CreateEvent("Warning", "капча / блок IP")),
            EventsIndexBuilder.MapEvent(CreateEvent("Error", "Ошибка аккаунта Avito 1: disk space"))
        };

        var model = EventsIndexBuilder.Build(
            rows,
            new() { Level = "warning" },
            page: 1,
            journalView: "warnings");

        Assert.Single(model.Events);
        Assert.Equal("warning", model.Events[0].Level);
    }

    [Fact]
    public void EventsFilter_ProblemsLevel_IncludesWarningAndError()
    {
        var rows = new[]
        {
            EventsIndexBuilder.MapEvent(CreateEvent("Warning", "капча / блок IP")),
            EventsIndexBuilder.MapEvent(CreateEvent("Error", "Ошибка аккаунта Avito 1: disk space")),
            EventsIndexBuilder.MapEvent(CreateEvent("Success", "Новый отклик"))
        };

        var model = EventsIndexBuilder.Build(
            rows,
            new() { Level = "errors" },
            page: 1);

        Assert.Equal(2, model.Events.Count);
    }

    private static WorkerEventListItem CreateEvent(string level, string message) =>
        new(
            Guid.NewGuid(),
            WorkerId,
            "WM1",
            null,
            null,
            level,
            message,
            null,
            OccurredAt);
}