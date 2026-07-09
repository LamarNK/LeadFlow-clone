using System.Collections.Concurrent;
using LeadFlow.Core.Services.Worker;

namespace Orbita.Worker.Services;

public sealed class BrowserMonitorSource : IBrowserMonitorSource
{
    private readonly ConcurrentDictionary<Guid, BrowserRegistration> _registrations = new();
    private int _activeSessions;

    public bool IsActive => Volatile.Read(ref _activeSessions) > 0;

    public void BeginSession()
    {
        Interlocked.Increment(ref _activeSessions);
    }

    public void EndSession()
    {
        if (Interlocked.Decrement(ref _activeSessions) <= 0)
        {
            Interlocked.Exchange(ref _activeSessions, 0);
            _registrations.Clear();
        }
    }

    public void Register(
        Guid accountId,
        string accountName,
        string adsPowerProfileId,
        Func<CancellationToken, Task<BrowserMonitorCapture?>> captureAsync)
    {
        if (!IsActive)
        {
            return;
        }

        _registrations[accountId] = new BrowserRegistration(
            accountId,
            accountName,
            adsPowerProfileId,
            captureAsync);
    }

    public void Unregister(Guid accountId)
    {
        _registrations.TryRemove(accountId, out _);
    }

    public IReadOnlyList<BrowserRegistration> GetRegistrations() =>
        _registrations.Values
            .OrderBy(x => x.AccountName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public sealed record BrowserRegistration(
        Guid AccountId,
        string AccountName,
        string AdsPowerProfileId,
        Func<CancellationToken, Task<BrowserMonitorCapture?>> CaptureAsync);
}