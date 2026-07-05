using Microsoft.Extensions.Caching.Memory;
using Orbita.Contracts;

namespace Orbita.Web.Services;

public sealed class BitrixValidationResultCache(IMemoryCache cache)
{
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(10);
    private const string KeyPrefix = "bitrix-validation:";

    public string Store(string draftWebhookUrl, BitrixWebhookValidationDto? validation)
    {
        var key = Guid.NewGuid().ToString("N");
        cache.Set(
            KeyPrefix + key,
            new BitrixValidationCacheEntry(draftWebhookUrl, validation),
            EntryLifetime);
        return key;
    }

    public BitrixValidationCacheEntry? Take(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var cacheKey = KeyPrefix + key;
        if (!cache.TryGetValue(cacheKey, out BitrixValidationCacheEntry? entry))
        {
            return null;
        }

        cache.Remove(cacheKey);
        return entry;
    }
}

public sealed record BitrixValidationCacheEntry(
    string DraftWebhookUrl,
    BitrixWebhookValidationDto? Validation);