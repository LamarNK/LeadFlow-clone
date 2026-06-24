using Orbita.Api.Services;

namespace Orbita.Tests;

public class ApiKeyServiceTests
{
    [Fact]
    public void VerifyApiKey_matches_hash()
    {
        var key = ApiKeyService.GenerateApiKey();
        var hash = ApiKeyService.HashApiKey(key);
        Assert.True(ApiKeyService.VerifyApiKey(key, hash));
        Assert.False(ApiKeyService.VerifyApiKey("wrong", hash));
    }
}