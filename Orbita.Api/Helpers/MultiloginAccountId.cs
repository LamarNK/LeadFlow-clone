using System.Security.Cryptography;
using System.Text;

namespace Orbita.Api.Helpers;

public static class MultiloginAccountId
{
    public const string IdNamespace = "orbita-multilogin:";

    public static Guid ToAccountGuid(string multiloginProfileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(multiloginProfileId);

        var normalized = multiloginProfileId.Trim();
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(IdNamespace + normalized));
        return new Guid(bytes);
    }
}
