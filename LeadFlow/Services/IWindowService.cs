using System.Windows;
using LeadFlow.Models;

namespace LeadFlow.Services;

public interface IWindowService
{
    Task ShowSettingsAsync(Window owner, CancellationToken cancellationToken);
    /// <summary>Немодальное окно с вкладками: новый аккаунт — новая вкладка (или фокус на уже открытом).</summary>
    Task ShowAvitoAuthAsync(Window owner, AvitoAccount account, CancellationToken cancellationToken);
    Task ShowAccountSettingsAsync(Window owner, AvitoAccount account, CancellationToken cancellationToken);
    /// <summary>Вкладка профиля Avito в общем окне браузера (отдельный WebView2 на вкладку).</summary>
    Task ShowAvitoProfileAsync(Window owner, AvitoAccount account, CancellationToken cancellationToken);
    Task ShowAvitoProfileAsync(
        Window owner,
        AvitoAccount account,
        string initialUrl,
        CancellationToken cancellationToken,
        string? avitoSubProfileId = null);
    /// <summary>Закрывает вкладки окна Avito для аккаунта и освобождает WebView2 (папка профиля не держится процессом).</summary>
    void CloseAvitoBrowserTabsForAccount(Guid accountId);
    Task ShowMonitoringAsync(
        Window owner,
        CancellationToken cancellationToken,
        MonitoringWindowLaunchRequest? launchRequest = null);
    Task ShowCandidateDetailsAsync(Window owner, CancellationToken cancellationToken);
    Task ShowDuplicateCheckAsync(Window owner, CancellationToken cancellationToken);
    Task ShowBitrixIntegrationAsync(Window owner, CancellationToken cancellationToken);
    Task ShowJournalAsync(Window owner, CancellationToken cancellationToken);
    Task ShowStatisticsHistoryAsync(Window owner, CancellationToken cancellationToken);
}
