

namespace LeadFlow.Services;

public interface ISettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
    string GetSettingsPath();
}
