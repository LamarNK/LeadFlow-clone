using LeadFlow.Core.Services;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoMousePathTests
{
    [Fact]
    public void BuildPath_EndsExactlyAtTarget()
    {
        for (var seed = 0; seed < 25; seed++)
        {
            var rnd = new Random(seed);
            var path = AvitoMousePath.BuildPath(100, 200, 700, 500, rnd);
            Assert.NotEmpty(path);
            var last = path[^1];
            Assert.Equal(700, last.X);
            Assert.Equal(500, last.Y);
        }
    }

    [Fact]
    public void BuildPath_ShortDistance_SinglePoint()
    {
        var path = AvitoMousePath.BuildPath(10, 10, 12, 11, new Random(1));
        var single = Assert.Single(path);
        Assert.Equal(12, single.X);
        Assert.Equal(11, single.Y);
    }

    [Fact]
    public void BuildPath_PointsStayWithinCorridor()
    {
        const decimal x0 = 0, y0 = 0, x1 = 600, y1 = 300;
        for (var seed = 0; seed < 50; seed++)
        {
            var path = AvitoMousePath.BuildPath(x0, y0, x1, y1, new Random(seed));
            foreach (var point in path)
            {
                // Поперечный шум до ~30% + продольный ±12%: коридор с запасом.
                Assert.InRange(point.X, -250, 850);
                Assert.InRange(point.Y, -250, 550);
            }
        }
    }

    [Fact]
    public void BuildPath_HasIntermediateWaypointsForLongMoves()
    {
        var path = AvitoMousePath.BuildPath(0, 0, 800, 400, new Random(42));
        Assert.True(path.Count >= 2, $"Ожидались промежуточные точки, получено {path.Count}.");
    }

    [Fact]
    public void BuildOvershoot_PointsBeyondTargetAlongDirection()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var overshoot = AvitoMousePath.BuildOvershoot(0, 0, 500, 0, new Random(seed));
            Assert.True(overshoot.X > 500 && overshoot.X <= 520, $"X={overshoot.X}");
            Assert.Equal(0, overshoot.Y);
        }
    }
}

public sealed class AvitoPersonaTests
{
    [Theory]
    [InlineData("account-a")]
    [InlineData("account-b")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    public void ResolveFactor_StaysWithinBoundsAndIsStable(string accountId)
    {
        var first = AvitoPersona.ResolveFactor(accountId);
        var second = AvitoPersona.ResolveFactor(accountId);
        Assert.Equal(first, second);
        if (accountId.Length == 0)
        {
            Assert.Equal(1.0, first);
        }
        else
        {
            Assert.InRange(first, 0.8, 1.3000001);
        }
    }

    [Fact]
    public void ResolveFactor_DifferentAccountsSpread()
    {
        var factors = Enumerable.Range(0, 50)
            .Select(i => AvitoPersona.ResolveFactor($"acct-{i}"))
            .Distinct()
            .ToList();
        Assert.True(factors.Count > 10, $"Ожидался разброс, получено {factors.Count} уникальных факторов.");
    }

    [Fact]
    public async Task Begin_AppliesFactorToHumanDelayAndRestores()
    {
        // Фактор < 1 уменьшает верхнюю границу — пауза короче базовой.
        string accountId = null!;
        for (var i = 0; i < 100; i++)
        {
            if (AvitoPersona.ResolveFactor($"fast-{i}") < 0.9)
            {
                accountId = $"fast-{i}";
                break;
            }
        }

        Assert.NotNull(accountId);
        using (AvitoPersona.Begin(accountId))
        {
            Assert.True(AvitoPersona.TimingFactor < 0.9);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await HumanDelay.DelayAsync(200, 220);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 260, $"Пауза не ужата фактором: {sw.ElapsedMilliseconds} ms.");
        }

        Assert.Equal(1.0, AvitoPersona.TimingFactor);
    }
}
