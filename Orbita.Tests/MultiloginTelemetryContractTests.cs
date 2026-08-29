using System.Reflection;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class MultiloginTelemetryContractTests
{
    [Theory]
    [InlineData(typeof(WorkerAccountDto))]
    [InlineData(typeof(WorkerDetail))]
    public void PublicTelemetryDtos_DoNotExposeMultiloginAutomationToken(Type dtoType)
    {
        var names = dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("MultiloginAutomationToken", names);
        Assert.DoesNotContain("AutomationToken", names);
    }

    [Fact]
    public void WorkerAccountSyncItemDto_CarriesMultiloginIds_WithoutToken()
    {
        var names = typeof(WorkerAccountSyncItemDto).GetProperties()
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(WorkerAccountSyncItemDto.MultiloginProfileId), names);
        Assert.Contains(nameof(WorkerAccountSyncItemDto.MultiloginFolderId), names);
        Assert.Contains(nameof(WorkerAccountSyncItemDto.MultiloginProfileName), names);
        Assert.DoesNotContain("MultiloginAutomationToken", names);
        Assert.DoesNotContain("AutomationToken", names);
        Assert.DoesNotContain("ProjectId", names);
    }

    [Fact]
    public void WorkerAccountSyncRequest_ExposesReplaceFlag_WithoutToken()
    {
        var names = typeof(WorkerAccountSyncRequest).GetProperties()
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(WorkerAccountSyncRequest.ReplaceMultiloginCatalog), names);
        Assert.DoesNotContain("MultiloginAutomationToken", names);
        Assert.DoesNotContain("AutomationToken", names);
    }

    [Fact]
    public void WorkerDetail_ExposesHasTokenFlag_WithoutTokenValue()
    {
        var names = typeof(WorkerDetail).GetProperties()
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(WorkerDetail.HasMultiloginAutomationToken), names);
        Assert.Contains(nameof(WorkerDetail.MultiloginLauncherUrl), names);
        Assert.Contains(nameof(WorkerDetail.MultiloginCloudApiUrl), names);
        Assert.Contains(nameof(WorkerDetail.LocalChromeExecutablePath), names);
        Assert.DoesNotContain("MultiloginAutomationToken", names);
    }

    [Fact]
    public void WorkerAccountConfigDto_DoesNotExposeAutomationToken()
    {
        var names = typeof(WorkerAccountConfigDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("MultiloginAutomationToken", names);
        Assert.DoesNotContain("AutomationToken", names);
        Assert.Contains(nameof(WorkerAccountConfigDto.MultiloginProfileId), names);
        Assert.Contains(nameof(WorkerAccountConfigDto.MultiloginFolderId), names);
        Assert.Contains(nameof(WorkerAccountConfigDto.LocalUserDataDir), names);
    }

    [Fact]
    public void WorkerConfigDto_MayCarryWorkerScopedToken_UnlikeTelemetry()
    {
        var configNames = typeof(WorkerConfigDto).GetProperties()
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(WorkerConfigDto.MultiloginAutomationToken), configNames);
        Assert.Contains(nameof(WorkerConfigDto.AdsPowerApiKey), configNames);

        var telemetryNames = typeof(WorkerAccountDto).GetProperties()
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("MultiloginAutomationToken", telemetryNames);
        Assert.DoesNotContain(nameof(WorkerConfigDto.MultiloginAutomationToken), telemetryNames);
    }

    [Fact]
    public void WorkerAccountDto_CarriesMultiloginIds_WithoutTokenOrUrls()
    {
        var names = typeof(WorkerAccountDto).GetProperties()
            .Select(static p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(WorkerAccountDto.AdsPowerProfileId), names);
        Assert.Contains(nameof(WorkerAccountDto.MultiloginProfileId), names);
        Assert.Contains(nameof(WorkerAccountDto.MultiloginFolderId), names);
        Assert.Contains(nameof(WorkerAccountDto.LocalUserDataDir), names);
        Assert.DoesNotContain("MultiloginAutomationToken", names);
        Assert.DoesNotContain("AutomationToken", names);
        Assert.DoesNotContain("MultiloginLauncherUrl", names);
        Assert.DoesNotContain("MultiloginCloudApiUrl", names);
        Assert.DoesNotContain("Proxy", names);
        Assert.DoesNotContain("Password", names);
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
        Assert.Null(account.LocalUserDataDir);
        Assert.Null(worker.LocalChromeExecutablePath);
    }
}
