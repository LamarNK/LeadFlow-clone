using System.Windows;
using LeadFlow.Models;
using LeadFlow.ViewModels;
using LeadFlow.Views;
using Microsoft.Extensions.DependencyInjection;

namespace LeadFlow.Services;

public sealed class WindowService(IServiceProvider serviceProvider) : IWindowService
{
    private readonly Dictionary<Type, Window> _openWindows = new();

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
        var window = ActivatorUtilities.CreateInstance<AvitoAuthWindow>(serviceProvider);
        var viewModel = ActivatorUtilities.CreateInstance<AvitoAuthViewModel>(serviceProvider);
        viewModel.ConfigureForAuthorization(account);
        window.Owner = owner;
        window.DataContext = viewModel;
        window.ShowDialog();
        return Task.CompletedTask;
    }

    public Task ShowAvitoProfileAsync(Window owner, AvitoAccount account, CancellationToken cancellationToken)
    {
        var window = ActivatorUtilities.CreateInstance<AvitoAuthWindow>(serviceProvider);
        var viewModel = ActivatorUtilities.CreateInstance<AvitoAuthViewModel>(serviceProvider);
        viewModel.ConfigureForProfile(account);
        window.Owner = owner;
        window.DataContext = viewModel;
        window.ShowDialog();
        return Task.CompletedTask;
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
