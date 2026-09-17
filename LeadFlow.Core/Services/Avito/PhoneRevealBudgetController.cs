namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Управляет бюджетом раскрытия телефонов внутри одного прохода.
/// Решение «останавливаться ли» всегда принимает ЭТОТ класс: сначала он пытается
/// поднять бюджет по хвосту замаскированных карточек и только потом отвечает,
/// исчерпан ли лимит. Так преждевременный break в цикле кликов не может
/// «съесть» адаптивный подъём.
/// </summary>
/// <param name="initialBudget">Бюджет субпрофиля (может быть поднят числом phone-watch).</param>
/// <param name="hardCeiling">
/// Остаток бюджета всего прохода аккаунта (<see cref="AvitoAccountPassBudget"/>).
/// Адаптивный подъём и phone-watch не могут вывести EffectiveBudget за этот потолок:
/// сумма кликов всех субпрофилей и повторных попыток ограничена на уровне аккаунта.
/// null — потолка нет (одиночные вызовы без контекста прохода).
/// </param>
internal sealed class PhoneRevealBudgetController(int initialBudget, int? hardCeiling = null)
{
    private bool _raised;
    // null — потолка нет (одиночные вызовы без контекста прохода); 0 — бюджет
    // прохода исчерпан, кликов больше нельзя (не «безлимит»).
    private readonly int? _hardCeiling = hardCeiling is >= 0 ? hardCeiling : null;

    public int EffectiveBudget { get; private set; } = ClampInitial(initialBudget, hardCeiling);

    private static int ClampInitial(int budget, int? ceiling)
    {
        budget = Math.Max(0, budget);
        return ceiling is >= 0 ? Math.Min(budget, ceiling.Value) : budget;
    }

    /// <summary>Бюджет был поднят по хвосту в этом проходе (однократно).</summary>
    public bool BudgetRaised => _raised;

    /// <summary>
    /// true — кликов хватит, пора останавливаться. false — можно кликать ещё:
    /// при первом исчерпании пытаемся однократно поднять бюджет по backlog.
    /// </summary>
    public bool ShouldStop(int clicksTotal, int maskedPending)
    {
        if (clicksTotal < EffectiveBudget)
        {
            return false;
        }

        if (!_raised)
        {
            _raised = true;
            var raised = CandidatePhoneRevealBudget.ResolveAdaptive(EffectiveBudget, maskedPending);
            if (_hardCeiling is { } ceiling && raised > ceiling)
            {
                raised = ceiling;
            }

            if (raised > EffectiveBudget)
            {
                EffectiveBudget = raised;
            }
        }

        return clicksTotal >= EffectiveBudget;
    }
}
