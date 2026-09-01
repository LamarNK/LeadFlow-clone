using Microsoft.AspNetCore.DataProtection;

namespace Orbita.Api.Services;

/// <summary>Шифрует пароль Avito и пароль локального прокси at rest (ASP.NET Data Protection).</summary>
public sealed class AvitoAccountSecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _avito = provider.CreateProtector("Orbita.WorkerAccount.AvitoPassword.v1");
    private readonly IDataProtector _proxy = provider.CreateProtector("Orbita.WorkerAccount.LocalProxyPassword.v1");

    public string Protect(string plaintext) => _avito.Protect(plaintext);

    public string Unprotect(string protectedValue) => _avito.Unprotect(protectedValue);

    public bool TryUnprotect(string? protectedValue, out string? plaintext) =>
        TryUnprotect(_avito, protectedValue, out plaintext);

    public string ProtectProxyPassword(string plaintext) => _proxy.Protect(plaintext);

    public bool TryUnprotectProxyPassword(string? protectedValue, out string? plaintext) =>
        TryUnprotect(_proxy, protectedValue, out plaintext);

    private static bool TryUnprotect(IDataProtector protector, string? protectedValue, out string? plaintext)
    {
        plaintext = null;
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return false;
        }

        try
        {
            plaintext = protector.Unprotect(protectedValue);
            return !string.IsNullOrEmpty(plaintext);
        }
        catch
        {
            return false;
        }
    }
}
