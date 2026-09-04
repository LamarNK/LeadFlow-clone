using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class OrbitaQueryCacheTests
{
    [Fact]
    public void BuildDataKey_DoesNotExposeAudienceOrSearchText()
    {
        var key = OrbitaQueryCache.BuildDataKey(
            OrbitaCacheDomain.Responses,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "user:manager@example.test",
            7,
            new { Search = "private candidate name", Page = 1 });

        Assert.DoesNotContain("manager@example.test", key, StringComparison.Ordinal);
        Assert.DoesNotContain("private candidate name", key, StringComparison.Ordinal);
        Assert.Contains(":v7:", key, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDataKey_SeparatesOfficeAudienceAndVersion()
    {
        var first = OrbitaQueryCache.BuildDataKey(OrbitaCacheDomain.Crm, Guid.NewGuid(), "manager-a", 1, new { Page = 1 });
        var second = OrbitaQueryCache.BuildDataKey(OrbitaCacheDomain.Crm, Guid.NewGuid(), "manager-a", 1, new { Page = 1 });
        var third = OrbitaQueryCache.BuildDataKey(OrbitaCacheDomain.Crm, Guid.NewGuid(), "manager-b", 2, new { Page = 1 });

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, third);
    }

    [Fact]
    public void MapDomains_CrmAndResponseChangesInvalidateDependentReadModels()
    {
        var domains = OrbitaQueryCache.MapDomains([PanelChangeKind.Crm, PanelChangeKind.Responses]);

        Assert.Contains(OrbitaCacheDomain.Crm, domains);
        Assert.Contains(OrbitaCacheDomain.Responses, domains);
        Assert.Contains(OrbitaCacheDomain.Dashboard, domains);
        Assert.Contains(OrbitaCacheDomain.Analytics, domains);
    }

    [Fact]
    public void MapDomains_TelemetryChangesUseRealtimeTtlInsteadOfInvalidatingDashboard()
    {
        var domains = OrbitaQueryCache.MapDomains(
            [PanelChangeKind.Workers, PanelChangeKind.Accounts, PanelChangeKind.Events, PanelChangeKind.Errors, PanelChangeKind.NavBadges]);

        Assert.DoesNotContain(OrbitaCacheDomain.Dashboard, domains);
    }

    [Fact]
    public void MapDomains_WorkerPageWritesInvalidateOnlyWorkerDetails()
    {
        var domains = OrbitaQueryCache.MapDomains(
            [PanelChangeKind.Accounts, PanelChangeKind.Events, PanelChangeKind.WorkerDetails]);

        Assert.Contains(OrbitaCacheDomain.WorkerDetails, domains);
        Assert.DoesNotContain(OrbitaCacheDomain.Dashboard, domains);
    }

    [Fact]
    public void MapDomains_TemplateWriteInvalidatesReferenceData()
    {
        var domains = OrbitaQueryCache.MapDomains([PanelChangeKind.Reference]);

        Assert.Equal([OrbitaCacheDomain.Reference], domains);
    }
}
