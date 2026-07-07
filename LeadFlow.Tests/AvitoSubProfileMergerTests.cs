using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoSubProfileMergerTests
{
    [Fact]
    public void Merge_PreservesBalanceAndIssues_FromExisting()
    {
        var existing = new List<AvitoSubProfile>
        {
            new()
            {
                Id = "sp-1",
                Name = "Old Name",
                Balance = 1500m,
                LastIssueKind = AvitoSubProfileIssueKind.SwitchFailed,
                LastIssueMessage = "switch failed",
                LastIssueAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        };

        var discovered = new List<AvitoSubProfile>
        {
            new() { Id = "sp-1", Name = "New Name", Category = "Работа", IsCurrent = true }
        };

        var merged = AvitoSubProfileMerger.Merge(existing, discovered);

        Assert.Single(merged);
        Assert.Equal("New Name", merged[0].Name);
        Assert.Equal("Работа", merged[0].Category);
        Assert.True(merged[0].IsCurrent);
        Assert.Equal(1500m, merged[0].Balance);
        Assert.Equal(AvitoSubProfileIssueKind.SwitchFailed, merged[0].LastIssueKind);
        Assert.Equal("switch failed", merged[0].LastIssueMessage);
    }

    [Fact]
    public void Merge_ReturnsExisting_WhenDiscoveredEmpty()
    {
        var existing = new List<AvitoSubProfile>
        {
            new() { Id = "sp-1", Name = "Keep", Balance = 100m }
        };

        var merged = AvitoSubProfileMerger.Merge(existing, []);

        Assert.Single(merged);
        Assert.Equal("Keep", merged[0].Name);
        Assert.Equal(100m, merged[0].Balance);
    }

    [Fact]
    public void Merge_IgnoresExistingWithEmptyIds_AndUsesDiscovered()
    {
        var existing = Enumerable.Range(0, 3)
            .Select(_ => new AvitoSubProfile { Id = string.Empty, Name = string.Empty, Balance = 10m })
            .ToList();
        var discovered = new List<AvitoSubProfile>
        {
            new() { Id = "101", Name = "Кадровый отдел", Category = "Работа", IsCurrent = true },
            new() { Id = "102", Name = "Служба России", Category = "Работа" }
        };

        var merged = AvitoSubProfileMerger.Merge(existing, discovered);

        Assert.Equal(2, merged.Count);
        Assert.Equal(["101", "102"], merged.Select(static x => x.Id).ToList());
        Assert.Null(merged[0].Balance);
    }

    [Fact]
    public void Merge_AddsNewProfiles_FromDiscovered()
    {
        var existing = new List<AvitoSubProfile>
        {
            new() { Id = "sp-1", Name = "One", Balance = 50m }
        };

        var discovered = new List<AvitoSubProfile>
        {
            new() { Id = "sp-1", Name = "One" },
            new() { Id = "sp-2", Name = "Two", Category = "Работа" }
        };

        var merged = AvitoSubProfileMerger.Merge(existing, discovered);

        Assert.Equal(2, merged.Count);
        Assert.Equal(50m, merged[0].Balance);
        Assert.Null(merged[1].Balance);
        Assert.Equal("Two", merged[1].Name);
    }
}