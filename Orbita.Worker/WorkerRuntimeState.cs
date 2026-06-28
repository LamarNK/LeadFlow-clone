namespace Orbita.Worker;

public sealed class WorkerRuntimeState
{
    private readonly Lock _sync = new();
    private string _status = "Подключение";
    private string _detail = string.Empty;
    private bool _isMonitoring;

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

    public string Tooltip
    {
        get
        {
            lock (_sync)
            {
                return string.IsNullOrWhiteSpace(_detail)
                    ? $"Орбита · {_status}"
                    : $"Орбита · {_status} · {_detail}";
            }
        }
    }
}