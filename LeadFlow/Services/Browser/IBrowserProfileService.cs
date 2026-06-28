using System.Threading.Tasks;

using Microsoft.Web.WebView2.Core;

namespace LeadFlow.Services.Browser;

public interface IBrowserProfileService
{
    BrowserProfileInfo GetProfile(AvitoAccount account);
    void DeleteProfile(string profilePath);
    
    /// <summary>
    /// Создаёт CoreWebView2Environment с учётом настроек аккаунта (прокси, профиль и т.д.)
    /// </summary>
    Task<CoreWebView2Environment> CreateEnvironmentAsync(AvitoAccount account);
}
