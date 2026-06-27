using Microsoft.AspNetCore.DataProtection;

namespace Orbita.Api.Services;

public sealed class WebhookSecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Orbita.PanelUserBitrix.WebhookUrl.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}