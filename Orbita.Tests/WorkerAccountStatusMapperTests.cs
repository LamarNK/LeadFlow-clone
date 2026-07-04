using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;

namespace Orbita.Tests;

public sealed class WorkerAccountStatusMapperTests
{
    [Fact]
    public void ResolveRuntimeStatus_EnabledWithPausedSnapshot_ReturnsAuthorized()
    {
        var status = WorkerAccountStatusMapper.ResolveRuntimeStatus(
            isEnabledInPanel: true,
            snapshotStatus: "Paused");

        Assert.Equal(AvitoAccountStatus.Authorized, status);
    }

    [Fact]
    public void ResolveRuntimeStatus_DisabledWithPausedSnapshot_StaysPaused()
    {
        var status = WorkerAccountStatusMapper.ResolveRuntimeStatus(
            isEnabledInPanel: false,
            snapshotStatus: "Paused");

        Assert.Equal(AvitoAccountStatus.Paused, status);
    }
}