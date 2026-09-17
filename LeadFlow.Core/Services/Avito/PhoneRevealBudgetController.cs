namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Управляет бюджетом раскрытия телефонов внутри одного прохода.
/// Решение «останавливаться ли» всегда принимает ЭТОТ класс: сначала он пытается
/// поднять бюджет по хвосту замаскированных карточек и только потом отвечает,
 /// исчерпан ли лимит. Так преждевременный break в цикле кликов не может
/// «съесть» адаптивный подъём.
/// </summary>
internal sealed class PhoneRevealBudgetController(int initialBudget)
{
    private bool _raised;

    public int EffectiveBudget { get; private set; } = Math.Max(0, initialBudget);

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
            if (raised > EffectiveBudget)
            {
                EffectiveBudget = raised;
            }
        }

        return clicksTotal >= EffectiveBudget;
    }
}
