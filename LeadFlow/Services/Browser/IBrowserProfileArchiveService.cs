

namespace LeadFlow.Services.Browser;

public interface IBrowserProfileArchiveService
{
    /// <summary>Папка профиля WebView2 + манифест с настройками аккаунта в один ZIP. Прогресс 0–100.</summary>
    Task ExportAsync(AvitoAccount account, string zipFilePath, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Распаковка в каталог профиля указанного аккаунта (обычно только что созданного). Прогресс 0–100.</summary>
    Task ImportAsync(AvitoAccount targetAccount, string zipFilePath, bool applyManifestToAccount, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Читает манифест из ZIP без распаковки (имя аккаунта, валидация формата).</summary>
    Task<AvitoProfileArchiveManifest> ReadManifestAsync(string zipFilePath, CancellationToken cancellationToken = default);
}
