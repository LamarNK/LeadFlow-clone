namespace LeadFlow.Core.Services.Avito;

/// <summary>Снимок состояния страницы Avito: URL, DOM-маркеры, модалка субпрофилей.</summary>
public sealed record AvitoPageState(
    AvitoPageKind PageKind,
    string? Url,
    string? Title,
    bool ProfileSwitchModalOpen,
    int ProfileCardsCount,
    string? CurrentSubProfileId,
    string? CurrentSubProfileName,
    int CandidatesItemCount,
    bool HasLoginForm,
    bool HasCaptcha,
    bool HasFirewallIp = false,
    bool HasInsufficientAdvance = false,
    bool HasEmailConfirmationRequired = false)
{
    public bool IsOnCandidates =>
        PageKind == AvitoPageKind.Candidates
        && !ProfileSwitchModalOpen;

    public string DescribeKindRu() => PageKind switch
    {
        _ when HasInsufficientAdvance && HasEmailConfirmationRequired => "объявления скрыты: недостаточно денег на авансе; требуется подтверждение почты",
        _ when HasInsufficientAdvance => "объявления скрыты: недостаточно денег на авансе",
        _ when HasEmailConfirmationRequired => "требуется подтверждение почты",
        AvitoPageKind.Candidates => "страница откликов",
        AvitoPageKind.Dashboard => "главная панель Avito Pro",
        AvitoPageKind.ProfileItems => "раздел «Мои объявления»",
        AvitoPageKind.ProfileSwitchModal => "модалка «Выбор профиля»",
        AvitoPageKind.Login => "форма входа",
        AvitoPageKind.Captcha when HasFirewallIp => "блок IP Avito",
        AvitoPageKind.Captcha => "капча",
        _ => "неизвестная страница"
    };

    public string DescribeForDiagnostics()
    {
        var parts = new List<string> { DescribeKindRu() };
        if (ProfileSwitchModalOpen)
        {
            parts.Add($"модалка открыта ({ProfileCardsCount} субпроф.)");
        }

        if (!string.IsNullOrWhiteSpace(CurrentSubProfileName))
        {
            parts.Add($"активный субпрофиль «{CurrentSubProfileName.Trim()}»");
        }
        else if (!string.IsNullOrWhiteSpace(CurrentSubProfileId))
        {
            parts.Add($"активный субпрофиль id={CurrentSubProfileId.Trim()}");
        }

        if (CandidatesItemCount > 0)
        {
            parts.Add($"карточек откликов: {CandidatesItemCount}");
        }

        if (!string.IsNullOrWhiteSpace(Url))
        {
            parts.Add(Url.Trim());
        }

        return string.Join(" · ", parts);
    }
}
