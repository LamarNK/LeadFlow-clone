using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

public sealed class WorkerUpdateGate
{
    private readonly Lock _sync = new();
    private string? _currentPhase;
    private bool _monitoringActive;

    public void SetPhase(string? phase)
    {
        lock (_sync)
        {
            _currentPhase = phase;
        }
    }

    public void SetMonitoringActive(bool active)
    {
        lock (_sync)
        {
            _monitoringActive = active;
        }
    }

    /// <summary>Фазы, во время которых идёт работа в браузере / с аккаунтом — MSI ставить нельзя.</summary>
    private static bool IsBusyPhase(string? phase) =>
        string.Equals(phase, WorkerActivityPhases.Cycle, StringComparison.Ordinal)
        || string.Equals(phase, WorkerActivityPhases.Account, StringComparison.Ordinal)
        || string.Equals(phase, WorkerActivityPhases.SubProfile, StringComparison.Ordinal)
        || string.Equals(phase, WorkerActivityPhases.Parallel, StringComparison.Ordinal);

    public bool IsSafeToApply
    {
        get
        {
            lock (_sync)
            {
                return !IsBusyPhase(_currentPhase);
            }
        }
    }

    public (string? Phase, bool MonitoringActive) GetSnapshot()
    {
        lock (_sync)
        {
            return (_currentPhase, _monitoringActive);
        }
    }
}