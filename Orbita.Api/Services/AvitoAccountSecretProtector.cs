using Microsoft.AspNetCore.DataProtection;

namespace Orbita.Api.Services;

/// <summary>Шифрует пароль Avito at rest (ASP.NET Data Protection).</summary>
public sealed class AvitoAccountSecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Orbita.WorkerAccount.AvitoPassword.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);

    public bool TryUnprotect(string? protectedValue, out string? plaintext)
    {
        plaintext = null;
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return false;
        }

        try
        {
            plaintext = _protector.Unprotect(protectedValue);
            return !string.IsNullOrEmpty(plaintext);
        }
        catch
        {
            return false;
        }
    }
}
