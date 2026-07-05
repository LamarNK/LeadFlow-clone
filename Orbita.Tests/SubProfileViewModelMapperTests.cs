using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Tests;

public sealed class SubProfileViewModelMapperTests
{
    [Fact]
    public void Map_UsesBalanceName_WhenSubProfileNameMissing()
    {
        var rows = SubProfileViewModelMapper.Map(
            [new WorkerSubProfileDto("", "", "Работа", false, null, null, null, null)],
            [new SubProfileBalanceDto("Служба России 3", 1000m)]);

        Assert.Single(rows);
        Assert.Equal("Служба России 3", rows[0].Name);
        Assert.Equal("Служба России 3", rows[0].Id);
    }

    [Fact]
    public void Map_DoesNotThrow_WhenBalanceNamesRepeat()
    {
        var subProfiles = new[]
        {
            new WorkerSubProfileDto("433959755", "Контракт РФ", "Работа", true, 527m, null, null, null),
            new WorkerSubProfileDto("434147235", "Контракт РФ", "Работа", false, null, null, null, null),
            new WorkerSubProfileDto("434056743", "Контракт РФ", "Работа", false, null, null, null, null)
        };
        var balances = new[]
        {
            new SubProfileBalanceDto("Контракт РФ", 527m),
            new SubProfileBalanceDto("Контракт РФ", null),
            new SubProfileBalanceDto("Контракт РФ", null)
        };

        var rows = SubProfileViewModelMapper.Map(subProfiles, balances);

        Assert.Equal(3, rows.Count);
        Assert.Equal("433959755", rows[0].Id);
        Assert.Equal("434147235", rows[1].Id);
        Assert.Equal("434056743", rows[2].Id);
        Assert.Contains("527", rows[0].BalanceText, StringComparison.Ordinal);
    }
}