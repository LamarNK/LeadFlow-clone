using System.Windows;
using LeadFlow.Models;

namespace LeadFlow.Services;

public interface IWindowService
{
    Task ShowSettingsAsync(Window owner, CancellationToken cancellationToken);
    Task ShowAvitoAuthAsync(Window owner, AvitoAccount account, CancellationToken cancellationToken);
    Task ShowAvitoProfileAsync(Window owner, AvitoAccount account, CancellationToken cancellationToken);
    Task ShowAvitoProfileAsync(Window owner, AvitoAccount account, string initialUrl, CancellationToken cancellationToken);
    Task ShowMonitoringAsync(Window owner, CancellationToken cancellationToken);
    Task ShowCandidateDetailsAsync(Window owner, CancellationToken cancellationToken);
    Task ShowDuplicateCheckAsync(Window owner, CancellationToken cancellationToken);
    Task ShowBitrixIntegrationAsync(Window owner, CancellationToken cancellationToken);
    Task ShowJournalAsync(Window owner, CancellationToken cancellationToken);
}
