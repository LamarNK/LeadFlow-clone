using System.Windows;
using LeadFlow.Models;
using LeadFlow.ViewModels;
using LeadFlow.Views;
using Microsoft.Extensions.DependencyInjection;

namespace LeadFlow.Services;

public sealed class WindowService(IServiceProvider serviceProvider) : IWindowService
{
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
        viewModel.Configure(account);
        window.Owner = owner;
        window.DataContext = viewModel;
        window.ShowDialog();
        return Task.CompletedTask;
    }

    public Task ShowMonitoringAsync(Window owner, CancellationToken cancellationToken)
    {
        var window = ActivatorUtilities.CreateInstance<MonitoringWindow>(serviceProvider);
        window.Owner = owner;
        window.Show();
        window.Activate();
        return Task.CompletedTask;
    }

    public Task ShowCandidateDetailsAsync(Window owner, CancellationToken cancellationToken)
    {
        var window = ActivatorUtilities.CreateInstance<CandidateDetailsWindow>(serviceProvider);
        window.Owner = owner;
        window.Show();
        window.Activate();
        return Task.CompletedTask;
    }

    public Task ShowDuplicateCheckAsync(Window owner, CancellationToken cancellationToken)
    {
        var window = ActivatorUtilities.CreateInstance<DuplicateCheckWindow>(serviceProvider);
        window.Owner = owner;
        window.Show();
        window.Activate();
        return Task.CompletedTask;
    }

    public Task ShowBitrixIntegrationAsync(Window owner, CancellationToken cancellationToken)
    {
        var window = ActivatorUtilities.CreateInstance<BitrixIntegrationWindow>(serviceProvider);
        window.Owner = owner;
        window.Show();
        window.Activate();
        return Task.CompletedTask;
    }

    public Task ShowJournalAsync(Window owner, CancellationToken cancellationToken)
    {
        var window = ActivatorUtilities.CreateInstance<JournalWindow>(serviceProvider);
        window.Owner = owner;
        window.Show();
        window.Activate();
        return Task.CompletedTask;
    }
}
