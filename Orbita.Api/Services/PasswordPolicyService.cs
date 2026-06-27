using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class PasswordPolicyService(IOptions<IdentityOptions> identityOptions)
{
    public PasswordPolicyDto GetPolicy()
    {
        var password = identityOptions.Value.Password;
        return new PasswordPolicyDto(
            password.RequiredLength,
            password.RequireDigit,
            password.RequireLowercase,
            password.RequireUppercase,
            password.RequireNonAlphanumeric,
            password.RequiredUniqueChars);
    }
}