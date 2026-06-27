using System.Security.Cryptography;
using System.Text;

namespace Orbita.Api.Helpers;

public static class AdsPowerAccountId
{
    public static Guid ToAccountGuid(string adsPowerProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adsPowerProfileId);

        var normalized = adsPowerProfileId.Trim();
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"orbita-adspower:{normalized}"));
        return new Guid(bytes);
    }
}