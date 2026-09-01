namespace LeadFlow.Core.Services.LocalChrome;

/// <summary>
/// Один обычный Chrome-профиль нельзя одновременно открыть для мониторинга и ручного входа.
/// </summary>
public sealed class LocalChromeAccountLock
{
    public const string Monitoring = "monitoring";
    public const string Login = "login";

    private readonly Dictionary<Guid, string> _owners = [];
    private readonly object _sync = new();

    public bool TryAcquire(Guid accountId, string purpose, out string? existingPurpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        lock (_sync)
        {
            if (_owners.TryGetValue(accountId, out var current))
            {
                existingPurpose = current;
                return string.Equals(current, purpose, StringComparison.Ordinal);
            }

            _owners[accountId] = purpose;
            existingPurpose = null;
            return true;
        }
    }

    public bool IsHeld(Guid accountId, string? purpose = null)
    {
        lock (_sync)
        {
            if (!_owners.TryGetValue(accountId, out var current))
            {
                return false;
            }

            return purpose is null
                || string.Equals(current, purpose, StringComparison.Ordinal);
        }
    }

    public void Release(Guid accountId, string purpose)
    {
        lock (_sync)
        {
            if (_owners.TryGetValue(accountId, out var current)
                && string.Equals(current, purpose, StringComparison.Ordinal))
            {
                _owners.Remove(accountId);
            }
        }
    }
}
