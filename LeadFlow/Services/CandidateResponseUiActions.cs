using System.Diagnostics;
using System.Windows;
using LeadFlow.Models;
using LeadFlow.Services.Bitrix;

namespace LeadFlow.Services;

public static class CandidateResponseUiActions
{
    public static void TryCopyPhone(CandidateResponse? response)
    {
        if (string.IsNullOrWhiteSpace(response?.PhoneRaw))
        {
            return;
        }

        Clipboard.SetText(response.PhoneRaw);
    }

    public static void TryOpenSourceUrl(CandidateResponse? response)
    {
        if (string.IsNullOrWhiteSpace(response?.SourceUrl))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(response.SourceUrl) { UseShellExecute = true });
    }

    public static async Task TryOpenBitrixAsync(
        CandidateResponse? response,
        ISettingsService settingsService,
        CancellationToken cancellationToken = default)
    {
        if (response is null || string.IsNullOrWhiteSpace(response.BitrixEntityId))
        {
            return;
        }

        var settings = await settingsService.LoadAsync(cancellationToken);
        var url = BitrixPortalLinks.TryBuildEntityDetailsUrl(
            settings.Bitrix.WebhookUrl,
            response.BitrixEntityType,
            response.BitrixEntityId);
        if (url is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static bool CanCopyPhone(CandidateResponse? response) =>
        !string.IsNullOrWhiteSpace(response?.PhoneRaw);

    public static bool CanOpenSource(CandidateResponse? response) =>
        !string.IsNullOrWhiteSpace(response?.SourceUrl);

    public static bool CanOpenBitrix(CandidateResponse? response) =>
        !string.IsNullOrWhiteSpace(response?.BitrixEntityId);
}
