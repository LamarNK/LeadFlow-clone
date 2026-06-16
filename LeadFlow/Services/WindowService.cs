using System.Windows;
using LeadFlow.Data;
using LeadFlow.Logging.Audit;
using LeadFlow.Models;
using LeadFlow.Services.AdsPower;
using LeadFlow.ViewModels;
using LeadFlow.Views;
using Microsoft.Extensions.DependencyInjection;

namespace LeadFlow.Services;

public sealed class WindowService(
    IServiceProvider serviceProvider,
    IAdsPowerApiClient adsPowerApiClient,
    IAdsPowerAvitoAuthService adsPowerAvitoAuthService,
    IAdsPowerAvitoAutomationService adsPowerAvitoAutomationService,
    AppRepository repository) : IWindowService
{
    private readonly Dictionary<Type, Window> _openWindows = new();
    private AvitoAuthWindow? _avitoBrowserWindow;

    /// <summary>
    /// Окна Avito не привязываем к окну настроек как к <see cref="Window.Owner"/>:
    /// при закрытии настроек WPF закрыл бы все дочерние немодальные окна.
    /// </summary>
    private static void SetAvitoWindowOwner(Window window, Window fallbackOwner)
    {
        window.Owner = Application.Current?.MainWindow ?? fallbackOwner;
    }

    public async Task<bool> ShowSettingsAsync(Window owner, CancellationToken cancellationToken)
    {
        var window = ActivatorUtilities.CreateInstance<SettingsWindow>(serviceProvider);
        var viewModel = ActivatorUtilities.CreateInstance<SettingsViewModel>(serviceProvider);
        window.Owner = owner;
        window.DataContext = viewModel;
        await viewModel.LoadAsync().ConfigureAwait(true);
        window.ShowDialog();
        return viewModel.SessionPersistedChanges;
    }

    public async Task ShowAvitoAuthAsync(Window owner, AvitoAccount account, CancellationToken cancellationToken)
    {
        if (account.ProfileProvider == AvitoProfileProvider.AdsPower)
        {
            await CheckAdsPowerAvitoAuthorizationAsync(account, cancellationToken).ConfigureAwait(true);
            return;
        }

        var host = GetOrCreateAvitoBrowserHost(owner);
        host.AddAuthTab(account);
        ActivateWindow(_avitoBrowserWindow!);
    }

    /// <summary>
    /// Запускает браузер AdsPower на /profile, через CDP читает имя пользователя и форму входа,
    /// сохраняет результат в аккаунт (DisplayName не трогаем — пишем отдельно <see cref="AvitoAccount.AvitoProfileName"/>).
    /// </summary>
    private async Task CheckAdsPowerAvitoAuthorizationAsync(AvitoAccount account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.AdsPowerProfileId) || string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl))
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower auth check skipped for account {account.DisplayName}: profile not configured.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(CheckAdsPowerAvitoAuthorizationAsync),
                filePath: "WindowService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "skipped_not_configured",
                    ["account.id"] = account.Id,
                    ["account.displayName"] = account.DisplayName
                });
            account.LastAuthCheckAt = DateTime.UtcNow;
            account.Status = AvitoAccountStatus.NotConfigured;
            account.LastErrorMessage = "Не задан профиль AdsPower или URL Local API.";
            await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(true);
            return;
        }

        var options = new AdsPowerConnectionOptions(
            account.AdsPowerApiBaseUrl,
            string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower auth check requested for account {account.DisplayName}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(CheckAdsPowerAvitoAuthorizationAsync),
            filePath: "WindowService.cs",
            properties: new Dictionary<string, object?>
            {
                ["step"] = "requested",
                ["account.id"] = account.Id,
                ["account.displayName"] = account.DisplayName,
                ["adsPower.userId"] = account.AdsPowerProfileId,
                ["adsPower.baseUrl"] = options.BaseUrl,
                ["adsPower.hasApiKey"] = !string.IsNullOrWhiteSpace(options.ApiKey)
            });

        AdsPowerAvitoAuthResult result;
        try
        {
            result = await adsPowerAvitoAuthService
                .CheckAuthorizationAsync(options, account.AdsPowerProfileId!, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower auth check threw unexpected exception for account {account.DisplayName}: {ex}",
                DeskLinkAuditLogLevel.Error,
                memberName: nameof(CheckAdsPowerAvitoAuthorizationAsync),
                filePath: "WindowService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "exception",
                    ["account.id"] = account.Id,
                    ["error.type"] = ex.GetType().FullName
                });
            account.LastAuthCheckAt = DateTime.UtcNow;
            account.Status = AvitoAccountStatus.Error;
            account.LastErrorMessage = $"Не удалось проверить авторизацию: {ex.Message}";
            await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(true);
            return;
        }

        account.LastAuthCheckAt = DateTime.UtcNow;
        if (result.IsAuthorized)
        {
            account.Status = AvitoAccountStatus.Authorized;
            // Сохраняем имя только если удалось распарсить — иначе оставляем то, что уже было.
            if (!string.IsNullOrWhiteSpace(result.ProfileName))
            {
                account.AvitoProfileName = result.ProfileName;
            }

            account.LastErrorMessage = string.IsNullOrWhiteSpace(result.ProfileName)
                ? "Авторизован. Имя профиля Avito не удалось распознать со страницы — это не мешает работе."
                : string.Empty;

            if (result.SubProfilesParsed)
            {
                account.SetSubProfiles(result.SubProfiles ?? Array.Empty<AvitoSubProfile>());
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower sub-profiles saved for {account.DisplayName}: count={account.SubProfilesCount}.",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(CheckAdsPowerAvitoAuthorizationAsync),
                    filePath: "WindowService.cs",
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "sub_profiles_saved",
                        ["account.id"] = account.Id,
                        ["subProfiles.count"] = account.SubProfilesCount
                    });
            }
        }
        else if (result.HasCaptcha)
        {
            account.Status = AvitoAccountStatus.RequiresManualAction;
            account.LastErrorMessage = result.KeepBrowserOpen
                ? "Avito показал капчу — пройдите проверку в открытом окне AdsPower и нажмите «Авторизовать в Avito» снова."
                : "Avito показал капчу — пройдите проверку в окне AdsPower и повторите.";
        }
        else if (result.HasLoginForm)
        {
            account.Status = AvitoAccountStatus.RequiresLogin;
            account.LastErrorMessage = result.KeepBrowserOpen
                ? "Войдите в Avito в открытом окне AdsPower и нажмите «Авторизовать в Avito» снова."
                : "Войдите в Avito в открывшемся окне AdsPower и нажмите «Авторизовать в Avito» снова.";
        }
        else
        {
            account.Status = AvitoAccountStatus.RequiresLogin;
            account.LastErrorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "Не удалось определить состояние авторизации Avito."
                : result.ErrorMessage!;
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower auth check result for {account.DisplayName}: status={account.Status}, avitoProfileName={account.AvitoProfileName ?? "<null>"}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(CheckAdsPowerAvitoAuthorizationAsync),
            filePath: "WindowService.cs",
            properties: new Dictionary<string, object?>
            {
                ["step"] = "applied",
                ["account.id"] = account.Id,
                ["account.displayName"] = account.DisplayName,
                ["account.avitoProfileName"] = account.AvitoProfileName,
                ["account.status"] = account.Status.ToString(),
                ["auth.isAuthorized"] = result.IsAuthorized,
                ["auth.hasLoginForm"] = result.HasLoginForm,
                ["auth.hasCaptcha"] = result.HasCaptcha,
                ["auth.currentUrl"] = result.CurrentUrl,
                ["auth.errorMessage"] = result.ErrorMessage
            });

        await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(true);
    }

    public Task ShowAccountSettingsAsync(Window owner, AvitoAccount account, CancellationToken cancellationToken)
    {
        var window = ActivatorUtilities.CreateInstance<AccountSettingsWindow>(serviceProvider);
        var viewModel = ActivatorUtilities.CreateInstance<AccountSettingsViewModel>(serviceProvider, account);
        window.Owner = owner;
        window.DataContext = viewModel;
        window.ShowDialog();
        return Task.CompletedTask;
    }

    public async Task ShowAvitoProfileAsync(Window owner, AvitoAccount account, CancellationToken cancellationToken)
    {
        if (account.ProfileProvider == AvitoProfileProvider.AdsPower)
        {
            await LaunchAdsPowerBrowserAsync(owner, account, account.AvitoResponsesUrl, avitoSubProfileId: null, cancellationToken)
                .ConfigureAwait(true);
            return;
        }

        var host = GetOrCreateAvitoBrowserHost(owner);
        host.AddProfileTab(account, initialUrl: null);
        ActivateWindow(_avitoBrowserWindow!);
    }

    public async Task ShowAvitoProfileAsync(
        Window owner,
        AvitoAccount account,
        string initialUrl,
        CancellationToken cancellationToken,
        string? avitoSubProfileId)
    {
        if (account.ProfileProvider == AvitoProfileProvider.AdsPower)
        {
            await LaunchAdsPowerBrowserAsync(owner, account, initialUrl, avitoSubProfileId, cancellationToken)
                .ConfigureAwait(true);
            return;
        }

        var host = GetOrCreateAvitoBrowserHost(owner);
        host.AddProfileTab(account, initialUrl);
        ActivateWindow(_avitoBrowserWindow!);
    }

    private async Task LaunchAdsPowerBrowserAsync(
        Window owner,
        AvitoAccount account,
        string? openUrl,
        string? avitoSubProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.AdsPowerProfileId) || string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl))
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower launch skipped for account {account.DisplayName}: profile not configured.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(LaunchAdsPowerBrowserAsync),
                filePath: "WindowService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "skipped_not_configured",
                    ["account.id"] = account.Id,
                    ["account.displayName"] = account.DisplayName
                });
            return;
        }

        try
        {
            var options = new AdsPowerConnectionOptions(
                account.AdsPowerApiBaseUrl,
                string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);

            if (!string.IsNullOrWhiteSpace(avitoSubProfileId))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower: перед открытием ссылки переключаем суб-профиль Avito (аккаунт {account.DisplayName}).",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(LaunchAdsPowerBrowserAsync),
                    filePath: "WindowService.cs",
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "sub_profile_switch",
                        ["account.displayName"] = account.DisplayName,
                        ["adsPower.userId"] = account.AdsPowerProfileId,
                        ["avito.subProfileId"] = avitoSubProfileId.Trim()
                    });
                try
                {
                    var switched = await adsPowerAvitoAutomationService
                        .SwitchActiveProfileAsync(
                            options,
                            account.AdsPowerProfileId!,
                            avitoSubProfileId.Trim(),
                            cancellationToken,
                            closeBrowserAfter: false)
                        .ConfigureAwait(true);
                    if (!switched)
                    {
                        _ = GlobalLogger.Instance.LogAsync(
                            $"AdsPower: переключение суб-профиля Avito не подтверждено (id={avitoSubProfileId.Trim()}), открываем URL в текущем кабинете.",
                            DeskLinkAuditLogLevel.Warning,
                            memberName: nameof(LaunchAdsPowerBrowserAsync),
                            filePath: "WindowService.cs",
                            properties: new Dictionary<string, object?>
                            {
                                ["account.displayName"] = account.DisplayName,
                                ["avito.subProfileId"] = avitoSubProfileId.Trim()
                            });
                    }
                }
                catch (AdsPowerDailyOpenLimitExceededException ex)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower: дневной лимит запусков — не удалось переключить суб-профиль перед открытием ссылки ({account.DisplayName}): {ex.Message}",
                        DeskLinkAuditLogLevel.Warning,
                        memberName: nameof(LaunchAdsPowerBrowserAsync),
                        filePath: "WindowService.cs",
                        errorKey: AdsPowerDailyOpenLimitExceededException.ErrorKey,
                        properties: new Dictionary<string, object?>
                        {
                            ["account.displayName"] = account.DisplayName,
                            ["avito.subProfileId"] = avitoSubProfileId.Trim(),
                            ["adsPower.apiCode"] = ex.ApiCode
                        });
                }
                catch (Exception ex)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower: не удалось переключить суб-профиль Avito перед открытием ссылки: {ex.Message}",
                        DeskLinkAuditLogLevel.Warning,
                        memberName: nameof(LaunchAdsPowerBrowserAsync),
                        filePath: "WindowService.cs",
                        properties: new Dictionary<string, object?>
                        {
                            ["account.displayName"] = account.DisplayName,
                            ["avito.subProfileId"] = avitoSubProfileId.Trim(),
                            ["error.type"] = ex.GetType().FullName
                        });
                }
            }

            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower browser launch requested for account {account.DisplayName}.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(LaunchAdsPowerBrowserAsync),
                filePath: "WindowService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["account.displayName"] = account.DisplayName,
                    ["adsPower.baseUrl"] = options.BaseUrl,
                    ["adsPower.hasApiKey"] = !string.IsNullOrWhiteSpace(options.ApiKey),
                    ["adsPower.userId"] = account.AdsPowerProfileId,
                    ["adsPower.openUrl"] = openUrl,
                    ["avito.subProfileId"] = string.IsNullOrWhiteSpace(avitoSubProfileId) ? null : avitoSubProfileId.Trim()
                });

            if (!string.IsNullOrWhiteSpace(openUrl))
            {
                // После SwitchActiveProfileAsync повторный browser/start с open_urls часто не открывает вкладку.
                await adsPowerAvitoAutomationService
                    .OpenUrlInRunningProfileAsync(options, account.AdsPowerProfileId!, openUrl.Trim(), cancellationToken)
                    .ConfigureAwait(true);
            }
            else
            {
                _ = await adsPowerApiClient
                    .StartBrowserAsync(options, account.AdsPowerProfileId, openUrl: null, cancellationToken)
                    .ConfigureAwait(true);
            }

            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower browser launch completed for account {account.DisplayName}.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(LaunchAdsPowerBrowserAsync),
                filePath: "WindowService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["account.displayName"] = account.DisplayName,
                    ["adsPower.baseUrl"] = options.BaseUrl,
                    ["adsPower.hasApiKey"] = !string.IsNullOrWhiteSpace(options.ApiKey),
                    ["adsPower.userId"] = account.AdsPowerProfileId,
                    ["adsPower.openUrl"] = openUrl
                });
        }
        catch (AdsPowerDailyOpenLimitExceededException ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower browser launch: дневной лимит запусков для аккаунта {account.DisplayName}. {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(LaunchAdsPowerBrowserAsync),
                filePath: "WindowService.cs",
                errorKey: AdsPowerDailyOpenLimitExceededException.ErrorKey,
                properties: new Dictionary<string, object?>
                {
                    ["account.displayName"] = account.DisplayName,
                    ["adsPower.baseUrl"] = account.AdsPowerApiBaseUrl,
                    ["adsPower.hasApiKey"] = !string.IsNullOrWhiteSpace(account.AdsPowerApiKey),
                    ["adsPower.userId"] = account.AdsPowerProfileId,
                    ["adsPower.openUrl"] = openUrl,
                    ["adsPower.apiCode"] = ex.ApiCode
                });
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower browser launch failed for account {account.DisplayName}.{Environment.NewLine}{ex}",
                DeskLinkAuditLogLevel.Error,
                memberName: nameof(LaunchAdsPowerBrowserAsync),
                filePath: "WindowService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["account.displayName"] = account.DisplayName,
                    ["adsPower.baseUrl"] = account.AdsPowerApiBaseUrl,
                    ["adsPower.hasApiKey"] = !string.IsNullOrWhiteSpace(account.AdsPowerApiKey),
                    ["adsPower.userId"] = account.AdsPowerProfileId,
                    ["adsPower.openUrl"] = openUrl
                });
        }
    }

    public void CloseAvitoBrowserTabsForAccount(Guid accountId)
    {
        if (_avitoBrowserWindow?.DataContext is AvitoBrowserHostViewModel host)
        {
            host.CloseTabsForAccount(accountId);
        }
    }

    private AvitoBrowserHostViewModel GetOrCreateAvitoBrowserHost(Window owner)
    {
        if (_avitoBrowserWindow is not null)
        {
            try
            {
                if (_avitoBrowserWindow.DataContext is AvitoBrowserHostViewModel existing)
                {
                    return existing;
                }
            }
            catch
            {
                // окно в неконсистентном состоянии
            }
        }

        _avitoBrowserWindow = ActivatorUtilities.CreateInstance<AvitoAuthWindow>(serviceProvider);
        var host = ActivatorUtilities.CreateInstance<AvitoBrowserHostViewModel>(serviceProvider);
        _avitoBrowserWindow.DataContext = host;
        host.AttachWindow(_avitoBrowserWindow);
        _avitoBrowserWindow.Closed += (_, _) => _avitoBrowserWindow = null;
        SetAvitoWindowOwner(_avitoBrowserWindow, owner);
        _avitoBrowserWindow.Show();
        return host;
    }

    public async Task ShowMonitoringAsync(
        Window owner,
        CancellationToken cancellationToken,
        MonitoringWindowLaunchRequest? launchRequest = null)
    {
        if (_openWindows.TryGetValue(typeof(MonitoringWindow), out var existingWindow))
        {
            if (existingWindow.DataContext is MonitoringViewModel existingVm)
            {
                if (launchRequest is not null)
                {
                    existingVm.ApplyLaunchRequest(launchRequest);
                    await existingVm.RefreshAsync().ConfigureAwait(true);
                }
            }

            ActivateWindow(existingWindow);
            return;
        }

        var window = ActivatorUtilities.CreateInstance<MonitoringWindow>(serviceProvider);
        window.Owner = owner;
        window.Closed += (_, _) => _openWindows.Remove(typeof(MonitoringWindow));
        _openWindows[typeof(MonitoringWindow)] = window;

        if (window.DataContext is MonitoringViewModel vm)
        {
            if (launchRequest is not null)
            {
                vm.ApplyLaunchRequest(launchRequest);
            }

            await vm.RefreshAsync().ConfigureAwait(true);
        }

        window.Show();
        ActivateWindow(window);
    }

    public Task ShowCandidateDetailsAsync(Window owner, CancellationToken cancellationToken)
    {
        ShowOrActivateWindow<CandidateDetailsWindow>(owner);
        return Task.CompletedTask;
    }

    public Task ShowDuplicateCheckAsync(Window owner, CancellationToken cancellationToken)
    {
        ShowOrActivateWindow<DuplicateCheckWindow>(owner);
        return Task.CompletedTask;
    }

    public Task ShowBitrixIntegrationAsync(Window owner, CancellationToken cancellationToken)
    {
        ShowOrActivateWindow<BitrixIntegrationWindow>(owner);
        return Task.CompletedTask;
    }

    public Task ShowJournalAsync(Window owner, CancellationToken cancellationToken)
    {
        ShowOrActivateWindow<JournalWindow>(owner);
        return Task.CompletedTask;
    }

    public Task ShowStatisticsHistoryAsync(Window owner, CancellationToken cancellationToken)
    {
        if (_openWindows.TryGetValue(typeof(StatisticsHistoryWindow), out var existingWindow))
        {
            if (existingWindow.DataContext is StatisticsHistoryViewModel vm)
            {
                _ = vm.RefreshAsync();
            }

            ActivateWindow(existingWindow);
            return Task.CompletedTask;
        }

        var window = ActivatorUtilities.CreateInstance<StatisticsHistoryWindow>(serviceProvider);
        window.Owner = owner;
        window.Closed += (_, _) => _openWindows.Remove(typeof(StatisticsHistoryWindow));
        _openWindows[typeof(StatisticsHistoryWindow)] = window;
        window.Show();
        ActivateWindow(window);
        return Task.CompletedTask;
    }

    public async Task ShowBalanceDetailsAsync(Window owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        BalanceDetailsWindow? window;
        bool created;
        lock (_openWindows)
        {
            window = (BalanceDetailsWindow?)_openWindows.GetValueOrDefault(typeof(BalanceDetailsWindow));
            if (window is not null)
            {
                created = false;
            }
            else
            {
                window = ActivatorUtilities.CreateInstance<BalanceDetailsWindow>(serviceProvider);
                window.Owner = owner;
                window.Closed += (_, _) => { lock (_openWindows) _openWindows.Remove(typeof(BalanceDetailsWindow)); };
                _openWindows[typeof(BalanceDetailsWindow)] = window;
                created = true;
            }
        }

        if (window.DataContext is BalanceDetailsViewModel vm)
        {
            await vm.LoadAsync(cancellationToken);
        }

        if (created)
        {
            window.Show();
        }

        ActivateWindow(window);
    }

    private void ShowOrActivateWindow<TWindow>(Window owner)
        where TWindow : Window
    {
        if (_openWindows.TryGetValue(typeof(TWindow), out var existingWindow))
        {
            ActivateWindow(existingWindow);
            return;
        }

        var window = ActivatorUtilities.CreateInstance<TWindow>(serviceProvider);
        window.Owner = owner;
        window.Closed += (_, _) => _openWindows.Remove(typeof(TWindow));
        _openWindows[typeof(TWindow)] = window;

        window.Show();
        ActivateWindow(window);
    }

    private static void ActivateWindow(Window window)
    {
        if (window.WindowState == System.Windows.WindowState.Minimized)
        {
            window.WindowState = System.Windows.WindowState.Normal;
        }

        window.Show();
        window.Activate();
        window.Focus();
    }
}
