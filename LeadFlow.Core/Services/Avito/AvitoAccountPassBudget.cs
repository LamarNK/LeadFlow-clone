using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Счётчики действий на один ЛОГИЧЕСКИЙ проход аккаунта — все субпрофили, все
/// браузерные сессии и повторные попытки извлечения после восстановления страницы.
/// Привязан к проходу через <see cref="AvitoAccount.MonitoringPassStartedAtUtc"/>:
/// при возобновлении незавершённого прохода (новая сессия, рестарт воркера)
/// израсходованные счётчики восстанавливаются с аккаунта; новый проход начинается
/// с чистыми счётчиками. Счётчики пишутся прямо в поля аккаунта (write-through),
/// поэтому любой <c>SaveAccountAsync</c> в середине прохода фиксирует прогресс.
/// </summary>
/// <remarks>
/// Раскрытие телефонов не имеет числового потолка: счётчик хранится только для телеметрии.
/// Лимиты автоответов и перезапусков сессии сохраняются.
/// <para>
/// Необязательный колбэк <c>persistChanged</c> (см. <see cref="ForAccountPass"/>)
/// вызывается синхронно после каждой мутации счётчиков: воркер сразу записывает
/// состояние в runtime store / account-resume.json, чтобы аварийный останов сразу
/// после браузерного действия не терял списанный резерв.
/// </para>
/// </remarks>
public sealed class AvitoAccountPassBudget
{
    private readonly object gate = new();
    private readonly AvitoAccount? account;
    private readonly Action<AvitoAccount>? persistChanged;

    private AvitoAccountPassBudget(AvitoAccount? account, Action<AvitoAccount>? persistChanged)
    {
        this.account = account;
        this.persistChanged = persistChanged;
    }

    /// <summary>
    /// Бюджет на логический проход. Если у аккаунта проход не завершён
    /// (<see cref="MonitoringAccountResume.IsUnfinishedPass"/>), счётчики
    /// восстанавливаются; иначе — новый проход с чистыми счётчиками.
    /// </summary>
    public static AvitoAccountPassBudget ForAccountPass(
        AvitoAccount? account = null,
        Action<AvitoAccount>? persistChanged = null)
    {
        var budget = new AvitoAccountPassBudget(account, persistChanged);
        if (account is not null)
        {
            if (MonitoringAccountResume.IsUnfinishedPass(
                    account.MonitoringPassStartedAtUtc,
                    account.MonitoringPassFinishedAtUtc))
            {
                budget.PhoneRevealClicksSpent = Math.Max(0, account.MonitoringPassPhoneRevealClicksSpent);
                budget.AutoRepliesSpent = Math.Max(0, account.MonitoringPassAutoRepliesSpent);
                budget.SessionRestarts = Math.Max(0, account.MonitoringPassSessionRestarts);
            }
            else
            {
                budget.PhoneRevealClicksSpent = 0;
                budget.AutoRepliesSpent = 0;
                budget.SessionRestarts = 0;
            }

            budget.WriteThroughLocked();
        }

        return budget;
    }

    public int AutoRepliesCap => MonitoringTiming.MaxMessengerAutoRepliesPerAccountPass;

    public int SessionRestartCap => MonitoringTiming.MaxSessionRestartsPerAccountPass;

    public int PhoneRevealClicksSpent { get; private set; }

    public int AutoRepliesSpent { get; private set; }

    public int SessionRestarts { get; private set; }

    public int AutoRepliesRemaining
    {
        get
        {
            lock (gate)
            {
                return Math.Max(0, AutoRepliesCap - AutoRepliesSpent);
            }
        }
    }

    /// <summary>Учитывает попытки раскрытия для телеметрии, не ограничивая их.</summary>
    public void RecordPhoneRevealAttempts(int count = 1)
    {
        lock (gate)
        {
            PhoneRevealClicksSpent += Math.Max(0, count);
            WriteThroughLocked();
        }
    }

    /// <summary>Убирает из телеметрии заведомо несостоявшиеся попытки.</summary>
    public void UnrecordPhoneRevealAttempts(int count = 1)
    {
        lock (gate)
        {
            PhoneRevealClicksSpent = Math.Max(0, PhoneRevealClicksSpent - Math.Max(0, count));
            WriteThroughLocked();
        }
    }

    /// <summary>
    /// Резервирует один автоответ ДО клика «отправить». false — исчерпан: отправлять
    /// нельзя. При потере подтверждения (исключение/таймаут после клика) резерв
    /// остаётся списанным.
    /// </summary>
    public bool TryReserveAutoReply()
    {
        lock (gate)
        {
            if (AutoRepliesSpent >= AutoRepliesCap)
            {
                return false;
            }

            AutoRepliesSpent++;
            WriteThroughLocked();
            return true;
        }
    }

    /// <summary>Возвращает резерв автоответа при подтверждённом отказе отправки.</summary>
    public void RefundAutoReply()
    {
        lock (gate)
        {
            AutoRepliesSpent = Math.Max(0, AutoRepliesSpent - 1);
            WriteThroughLocked();
        }
    }

    /// <summary>
    /// Регистрирует перезапуск сценария после восстановления страницы.
    /// false — лимит перезапусков на проход исчерпан, сценарий должен завершиться.
    /// </summary>
    public bool TryRegisterSessionRestart()
    {
        lock (gate)
        {
            if (SessionRestarts >= SessionRestartCap)
            {
                return false;
            }

            SessionRestarts++;
            WriteThroughLocked();
            return true;
        }
    }

    public string Describe() =>
        $"phoneRevealClicks={PhoneRevealClicksSpent} (unlimited), " +
        $"autoReplies={AutoRepliesSpent}/{AutoRepliesCap}, " +
        $"sessionRestarts={SessionRestarts}/{SessionRestartCap}";

    private void WriteThroughLocked()
    {
        if (account is null)
        {
            return;
        }

        account.MonitoringPassPhoneRevealClicksSpent = PhoneRevealClicksSpent;
        account.MonitoringPassAutoRepliesSpent = AutoRepliesSpent;
        account.MonitoringPassSessionRestarts = SessionRestarts;
        // Мутация сразу уходит в персистентность (у воркера — синхронная запись файла
        // resume внутри SaveAccountAsync → runtimeStore.Upsert): окно «резерв списан,
        // процесс упал до обычного сохранения» закрывается. Callback не бросает
        // исключений (оборачивается вызывающей стороной), поэтому блокировка безопасна.
        persistChanged?.Invoke(account);
    }
}
