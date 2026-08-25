using System.Reflection;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class MultiloginTelemetryContractTests
{
    [Theory]
    [InlineData(typeof(WorkerAccountDto))]
    [InlineData(typeof(WorkerAccountConfigDto))]
    [InlineData(typeof(WorkerConfigDto))]
    [InlineData(typeof(WorkerDetail))]
    public void PublicWorkerDtos_DoNotExposeMultiloginAutomationToken(Type dtoType)
    {
        var names = dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("MultiloginAutomationToken", names);
        Assert.DoesNotContain("AutomationToken", names);
    }

    [Fact]
    public void WorkerAccountDto_StillExposesAdsPowerProfileId_WithoutMultiloginFields()
    {
        var names = typeof(WorkerAccountDto).GetProperties()
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(WorkerAccountDto.AdsPowerProfileId), names);
        Assert.DoesNotContain("MultiloginProfileId", names);
        Assert.DoesNotContain("MultiloginFolderId", names);
        Assert.DoesNotContain("MultiloginLauncherUrl", names);
        Assert.DoesNotContain("MultiloginCloudApiUrl", names);
    }

    [Fact]
    public void NewWorkerEntities_LeaveMultiloginFieldsNull_AndKeepAdsPowerDefaults()
    {
        var worker = new WorkerEntity();
        Assert.Null(worker.MultiloginLauncherUrl);
        Assert.Null(worker.MultiloginCloudApiUrl);
        Assert.Null(worker.MultiloginAutomationToken);
        Assert.Null(worker.AdsPowerApiBaseUrl);
        Assert.Null(worker.AdsPowerApiKey);

        var account = new WorkerAccountEntity();
        Assert.Equal(string.Empty, account.AdsPowerProfileId);
        Assert.Null(account.MultiloginProfileId);
        Assert.Null(account.MultiloginProfileName);
        Assert.Null(account.MultiloginFolderId);
    }
}
