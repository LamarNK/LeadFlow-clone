using System.Windows;
using LeadFlow.Models;
using LeadFlow.ViewModels;
using LeadFlow.Views;
using Microsoft.Extensions.DependencyInjection;

namespace LeadFlow.Services;

public sealed class WindowService(IServiceProvider serviceProvider) : IWindowService
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

    public Task ShowSettingsAsync(Window owner, CancellationToken cancellationToken)
    {
        var window = ActivatorUtilities.CreateInstance<SettingsWindow>(serviceProvider);
        var viewModel = ActivatorUtilities.CreateInstance<SettingsViewModel>(serviceProvider);
        window.Owner = owner;
        window.DataContext = viewModel;
        viewModel.LoadCommand.Execute(null);
        window.ShowDialog();
        return Task.CompletedTask;
    }

    public Task ShowAvitoAuthAsync(Window owner, AvitoAccount account, CancellationToken cancellationToken)
    {
        var host = GetOrCreateAvitoBrowserHost(owner);
        host.AddAuthTab(account);
        ActivateWindow(_avitoBrowserWindow!);
        return Task.CompletedTask;
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

    public Task ShowAvitoProfileAsync(Window owner, AvitoAccount account, CancellationToken cancellationToken)
    {
        var host = GetOrCreateAvitoBrowserHost(owner);
        host.AddProfileTab(account, initialUrl: null);
        ActivateWindow(_avitoBrowserWindow!);
        return Task.CompletedTask;
    }

    public Task ShowAvitoProfileAsync(Window owner, AvitoAccount account, string initialUrl, CancellationToken cancellationToken)
    {
        var host = GetOrCreateAvitoBrowserHost(owner);
        host.AddProfileTab(account, initialUrl);
        ActivateWindow(_avitoBrowserWindow!);
        return Task.CompletedTask;
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

    public Task ShowMonitoringAsync(Window owner, CancellationToken cancellationToken)
    {
        ShowOrActivateWindow<MonitoringWindow>(owner);
        return Task.CompletedTask;
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
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        window.Focus();
    }
}
