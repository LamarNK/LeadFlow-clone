using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Logging.Audit;
using LeadFlow.Services.AdsPower;

namespace LeadFlow.ViewModels;

public partial class AdsPowerProfilePickerViewModel(IAdsPowerApiClient apiClient) : ObservableObject
{
    public ObservableCollection<AdsPowerProfileSummary> Profiles { get; } = [];

    [ObservableProperty]
    private string apiBaseUrl = "http://127.0.0.1:50325";

    [ObservableProperty]
    private string? apiKey;

    [ObservableProperty]
    private string statusMessage = "Убедитесь, что AdsPower запущен, затем нажмите «Обновить список».";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private AdsPowerProfileSummary? selectedProfile;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Profiles.Clear();
            SelectedProfile = null;
            StatusMessage = "Загрузка списка профилей…";
            var options = new AdsPowerConnectionOptions(ApiBaseUrl.Trim(), string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim());
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile refresh requested for {options.BaseUrl}.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(RefreshAsync),
                filePath: "AdsPowerProfilePickerViewModel.cs",
                properties: new Dictionary<string, object?>
                {
                    ["adsPower.baseUrl"] = options.BaseUrl,
                    ["adsPower.hasApiKey"] = !string.IsNullOrWhiteSpace(options.ApiKey)
                });
            var list = await apiClient.ListProfilesAsync(options, CancellationToken.None).ConfigureAwait(true);
            foreach (var p in list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                Profiles.Add(p);
            }

            StatusMessage = list.Count == 0
                ? "Профили не найдены. Проверьте URL API и ключ (если включён в AdsPower)."
                : $"Загружено профилей: {list.Count}.";
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile refresh completed. Profiles loaded: {list.Count}.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(RefreshAsync),
                filePath: "AdsPowerProfilePickerViewModel.cs",
                properties: new Dictionary<string, object?>
                {
                    ["adsPower.baseUrl"] = options.BaseUrl,
                    ["adsPower.hasApiKey"] = !string.IsNullOrWhiteSpace(options.ApiKey),
                    ["adsPower.profileCount"] = list.Count
                });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Не удалось загрузить профили AdsPower: {ex.Message}";
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile refresh failed.{Environment.NewLine}{ex}",
                DeskLinkAuditLogLevel.Error,
                memberName: nameof(RefreshAsync),
                filePath: "AdsPowerProfilePickerViewModel.cs",
                properties: new Dictionary<string, object?>
                {
                    ["adsPower.baseUrl"] = ApiBaseUrl?.Trim(),
                    ["adsPower.hasApiKey"] = !string.IsNullOrWhiteSpace(ApiKey)
                });
        }
        finally
        {
            IsBusy = false;
        }
    }

    public bool TryBuildResult(out AdsPowerPickerResult? result)
    {
        result = null;
        if (SelectedProfile is null)
        {
            StatusMessage = "Выберите профиль в списке.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ApiBaseUrl))
        {
            StatusMessage = "Укажите базовый URL Local API.";
            return false;
        }

        result = new AdsPowerPickerResult(
            SelectedProfile.UserId,
            SelectedProfile.Name,
            ApiBaseUrl.Trim(),
            string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim());
        return true;
    }
}
