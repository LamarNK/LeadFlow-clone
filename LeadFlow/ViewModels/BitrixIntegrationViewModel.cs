using CommunityToolkit.Mvvm.ComponentModel;

using LeadFlow.Services;


namespace LeadFlow.ViewModels;

public partial class BitrixIntegrationViewModel(ICandidateParser candidateParser, ISettingsService settingsService) : ObservableObject
{
    [ObservableProperty]
    private BitrixLeadPreview preview = new();

    [ObservableProperty]
    private string statusText = "Ожидает проверки дублей";

    [ObservableProperty]
    private string entityId = string.Empty;

    [ObservableProperty]
    private string errorText = string.Empty;

    public async Task UpdateAsync(CandidateResponse? response)
    {
        if (response is null)
        {
            Preview = new BitrixLeadPreview();
            StatusText = "Ожидает проверки дублей";
            EntityId = string.Empty;
            ErrorText = string.Empty;
            return;
        }

        var settings = await settingsService.LoadAsync(CancellationToken.None);
        Preview = candidateParser.BuildPreview(response, settings.Bitrix);
        EntityId = response.BitrixEntityId;
        ErrorText = response.ErrorMessage;
        StatusText = response.Status switch
        {
            ResponseStatus.Duplicate => "Дубль найден",
            ResponseStatus.Sent => "Сделка создана",
            ResponseStatus.Error => "Ошибка отправки",
            ResponseStatus.InProgress => "Отправляется в Bitrix24",
            ResponseStatus.ActionRequired => "Требуется действие (отложено или контакт без сделки)",
            _ => "Ожидает проверки дублей"
        };
    }
}
