using Orbita.Contracts;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Наблюдает за состоянием сессии пополнения и отменяет связанный <see cref="CancellationToken"/>
/// как только сессия переходит в терминальное состояние (cancelled/expired/failed/paid) или исчезает.
/// Используется воркером, чтобы прервать уже запущенный браузерный сценарий до клика по оплате
/// и формирования QR, не давая позднему QrReady «оживить» отменённую сессию.
/// </summary>
public sealed class TopUpSessionCancellationGuard
{
    private readonly Func<CancellationToken, Task<TopUpSessionPollResult>> _sessionQuery;
    private readonly TimeSpan _pollInterval;
    private readonly int _maxConsecutiveFailures;
    private readonly CancellationTokenSource _cancelledCts = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly object _failureLock = new();
    private int _consecutiveFailures;

    public TopUpSessionCancellationGuard(
        Func<CancellationToken, Task<TopUpSessionPollResult>> sessionQuery,
        TimeSpan? pollInterval = null,
        int maxConsecutiveFailures = 5)
    {
        _sessionQuery = sessionQuery;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _maxConsecutiveFailures = maxConsecutiveFailures;
    }

    /// <summary>Токен, который отменяется при терминальном состоянии сессии.</summary>
    public CancellationToken Token => _cancelledCts.Token;

    /// <summary>Признак того, что сессия уже наблюдалась терминальной.</summary>
    public bool IsCancelled => _cancelledCts.IsCancellationRequested;

    /// <summary>Останавливает фоновое наблюдение (не сигнализирует об отмене сессии).</summary>
    public void Stop() => _stopCts.Cancel();

    /// <summary>
    /// Опрашивает состояние сессии до тех пор, пока она активна. Возвращает <c>false</c>,
    /// если сессия стала терминальной/отсутствует (или накоплен порог транзиентных ошибок),
    /// иначе <c>true</c> при остановке наблюдения или отмене внешним токеном.
    /// </summary>
    public async Task<bool> RunAsync(Guid sessionId, CancellationToken externalToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _stopCts.Token);
        while (!linked.IsCancellationRequested)
        {
            TopUpSessionPollResult result;
            try
            {
                result = await _sessionQuery(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Транспортное исключение — транзиентная ошибка.
                result = new TopUpSessionPollResult(TopUpSessionPollStatus.Transient);
            }

            if (ApplyResult(result))
            {
                return false;
            }

            try
            {
                await Task.Delay(_pollInterval, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                break;
            }
        }

        return !_cancelledCts.IsCancellationRequested;
    }

    /// <summary>Однократная проверка: активна ли сессия прямо сейчас.</summary>
    public async Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken ct)
    {
        TopUpSessionPollResult result;
        try
        {
            result = await _sessionQuery(ct).ConfigureAwait(false);
        }
        catch
        {
            // Транспортное исключение — транзиентная ошибка (не прерываем сценарий сразу).
            result = new TopUpSessionPollResult(TopUpSessionPollStatus.Transient);
        }

        // Используем ту же семантику счётчика ошибок, что и фоновый цикл: транзиентные ошибки
        // не отменяют до накопления порога; окончательные состояния отменяют немедленно.
        return !ApplyResult(result);
    }

    /// <summary>
    /// Применяет результат опроса к счётчику последовательных ошибок и решает, нужно ли
    /// отменить. Потокобезопасен: стартовая проверка и фоновый цикл могут пересекаться.
    /// Возвращает <c>true</c>, если следует отменить (и отменяет токен).
    /// </summary>
    private bool ApplyResult(TopUpSessionPollResult result)
    {
        switch (result.Status)
        {
            case TopUpSessionPollStatus.Active:
                lock (_failureLock)
                {
                    _consecutiveFailures = 0;
                }
                return false;

            case TopUpSessionPollStatus.Terminal:
            case TopUpSessionPollStatus.Missing:
            case TopUpSessionPollStatus.Permanent:
                // Окончательное состояние — отменяем немедленно.
                _cancelledCts.Cancel();
                return true;

            case TopUpSessionPollStatus.Transient:
                lock (_failureLock)
                {
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= _maxConsecutiveFailures)
                    {
                        _cancelledCts.Cancel();
                        return true;
                    }
                }
                return false;

            default:
                return false;
        }
    }
}
