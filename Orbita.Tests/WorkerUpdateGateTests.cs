using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerUpdateGateTests
{
    [Theory]
    [InlineData(WorkerActivityPhases.Waiting)]
    [InlineData(WorkerActivityPhases.Idle)]
    [InlineData(WorkerActivityPhases.Stopped)]
    [InlineData(WorkerActivityPhases.Error)]
    public void IsSafeToApply_True_WhenNotBusy_DuringMonitoring(string phase)
    {
        var gate = new WorkerUpdateGate();
        gate.SetMonitoringActive(true);
        gate.SetPhase(phase);

        Assert.True(gate.IsSafeToApply);
    }

    [Theory]
    [InlineData(WorkerActivityPhases.Cycle)]
    [InlineData(WorkerActivityPhases.Account)]
    [InlineData(WorkerActivityPhases.SubProfile)]
    [InlineData(WorkerActivityPhases.Parallel)]
    public void IsSafeToApply_False_WhenBusyDuringMonitoring(string phase)
    {
        var gate = new WorkerUpdateGate();
        gate.SetMonitoringActive(true);
        gate.SetPhase(phase);

        Assert.False(gate.IsSafeToApply);
    }

    [Fact]
    public void IsSafeToApply_True_WhenIdleAndMonitoringStopped()
    {
        var gate = new WorkerUpdateGate();
        gate.SetMonitoringActive(false);
        gate.SetPhase(WorkerActivityPhases.Idle);

        Assert.True(gate.IsSafeToApply);
    }
}