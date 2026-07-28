using Orbita.Contracts;

namespace Orbita.Tests;

/// <summary>Политика распределения — <see cref="CrmLeadDistribution"/>.</summary>
public sealed class CrmLeadDistributionTests
{
    [Fact]
    public void SelectManagerForNewLead_PicksLowestLoadRatio()
    {
        var decision = CrmLeadDistribution.SelectManagerForNewLead(
        [
            new("a", Capacity: 10, ActiveLoad: 9, LastAutoAssignedAtUtc: DateTime.UtcNow.AddHours(-1), IsOnShift: true),
            new("b", Capacity: 10, ActiveLoad: 2, LastAutoAssignedAtUtc: DateTime.UtcNow, IsOnShift: true),
            new("c", Capacity: 5, ActiveLoad: 5, LastAutoAssignedAtUtc: null, IsOnShift: true)
        ]);

        Assert.Equal("b", decision.ManagerUserId);
    }

    [Fact]
    public void SelectManagerForNewLead_SkipsOffShiftAndFull()
    {
        var decision = CrmLeadDistribution.SelectManagerForNewLead(
        [
            new("full", Capacity: 3, ActiveLoad: 3, LastAutoAssignedAtUtc: null, IsOnShift: true),
            new("off", Capacity: 10, ActiveLoad: 0, LastAutoAssignedAtUtc: null, IsOnShift: false),
            new("free", Capacity: 3, ActiveLoad: 1, LastAutoAssignedAtUtc: DateTime.UtcNow, IsOnShift: true)
        ]);

        Assert.Equal("free", decision.ManagerUserId);
    }

    [Fact]
    public void SelectManagerForNewLead_TieBreaksByOldestAssignment()
    {
        var older = DateTime.UtcNow.AddHours(-5);
        var newer = DateTime.UtcNow.AddMinutes(-5);
        var decision = CrmLeadDistribution.SelectManagerForNewLead(
        [
            new("new", Capacity: 10, ActiveLoad: 1, LastAutoAssignedAtUtc: newer, IsOnShift: true),
            new("old", Capacity: 10, ActiveLoad: 1, LastAutoAssignedAtUtc: older, IsOnShift: true)
        ]);

        Assert.Equal("old", decision.ManagerUserId);
    }

    [Fact]
    public void SelectManagerForNewLead_ReturnsNullWhenNobodyFree()
    {
        var decision = CrmLeadDistribution.SelectManagerForNewLead(
        [
            new("a", Capacity: 2, ActiveLoad: 2, LastAutoAssignedAtUtc: null, IsOnShift: true),
            new("b", Capacity: 1, ActiveLoad: 0, LastAutoAssignedAtUtc: null, IsOnShift: false)
        ]);

        Assert.Null(decision.ManagerUserId);
    }

    [Fact]
    public void FreeSlots_ClampsAtZero()
    {
        Assert.Equal(0, CrmLeadDistribution.FreeSlots(5, 7));
        Assert.Equal(3, CrmLeadDistribution.FreeSlots(5, 2));
    }

    [Fact]
    public void SelectCardsForShiftStart_TakesOldestFirst()
    {
        var t0 = DateTime.UtcNow.AddHours(-3);
        var t1 = DateTime.UtcNow.AddHours(-2);
        var t2 = DateTime.UtcNow.AddHours(-1);
        var id0 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var id1 = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var id2 = Guid.Parse("00000000-0000-0000-0000-000000000003");

        var selected = CrmLeadDistribution.SelectCardsForShiftStart(
        [
            new(id2, t2),
            new(id0, t0),
            new(id1, t1)
        ], freeSlots: 2);

        Assert.Equal([id0, id1], selected);
    }

    [Fact]
    public void SelectManagerUserId_CompatWrapper_StillWorks()
    {
        var selected = CrmLeadDistribution.SelectManagerUserId(
        [
            ("a", 10, 9, DateTime.UtcNow),
            ("b", 10, 1, DateTime.UtcNow)
        ]);
        Assert.Equal("b", selected);
    }
}
