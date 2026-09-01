namespace Orbita.Worker;

public sealed class WorkerRuntimeState
{
    private readonly Lock _sync = new();
    private string _status = "Подключение";
    private string _detail = string.Empty;
    private bool _isMonitoring;
    private DateTime? _nextCycleAtUtc;

    public event EventHandler? Changed;

    public string Status
    {
        get { lock (_sync) return _status; }
        set { lock (_sync) { _status = value; Changed?.Invoke(this, EventArgs.Empty); } }
    }

    public string Detail
    {
        get { lock (_sync) return _detail; }
        set { lock (_sync) { _detail = value; Changed?.Invoke(this, EventArgs.Empty); } }
    }

    public bool IsMonitoring
    {
        get { lock (_sync) return _isMonitoring; }
        set { lock (_sync) { _isMonitoring = value; Changed?.Invoke(this, EventArgs.Empty); } }
    }

    public DateTime? NextCycleAtUtc
    {
        get { lock (_sync) return _nextCycleAtUtc; }
        set { lock (_sync) { _nextCycleAtUtc = value; Changed?.Invoke(this, EventArgs.Empty); } }
    }

    public string Tooltip
    {
        get
        {
            lock (_sync)
            {
                var waiting = FormatWaitingFragment(_nextCycleAtUtc);
                if (waiting is not null)
                {
                    return $"Орбита · {_status} · {waiting}";
                }

                return string.IsNullOrWhiteSpace(_detail)
                    ? $"Орбита · {_status}"
                    : $"Орбита · {_status} · {_detail}";
            }
        }
    }

    private static string? FormatWaitingFragment(DateTime? nextCycleAtUtc)
    {
        if (nextCycleAtUtc is not DateTime next)
        {
            return null;
        }

        var nextUtc = next.Kind switch
        {
            DateTimeKind.Utc => next,
            DateTimeKind.Local => next.ToUniversalTime(),
            _ => DateTime.SpecifyKind(next, DateTimeKind.Utc)
        };

        var remaining = nextUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "цикл запускается";
        }

        var clock = nextUtc.ToLocalTime().ToString("HH:mm");
        if (remaining < TimeSpan.FromMinutes(1))
        {
            return $"пауза: меньше минуты · в {clock}";
        }

        return $"пауза: {(int)remaining.TotalMinutes} мин · в {clock}";
    }
}
