using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orbita.Api.Options;
using Orbita.Contracts;
using StackExchange.Redis;

namespace Orbita.Api.Services;

public enum OrbitaCacheDomain
{
    Dashboard,
    WorkerDetails,
    Responses,
    Crm,
    Analytics,
    Reference,
    Logs
}

public sealed record OrbitaCachePolicy(TimeSpan LocalTtl, TimeSpan DistributedTtl, bool UseLocalCache = true)
{
    public static readonly OrbitaCachePolicy Realtime = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    // Large account lists are valuable in Redis but must not inflate every API
    // instance's in-process cache. The distributed TTL still remains five seconds.
    public static readonly OrbitaCachePolicy LargeRealtime = new(TimeSpan.Zero, TimeSpan.FromSeconds(5), UseLocalCache: false);
    public static readonly OrbitaCachePolicy Interactive = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    public static readonly OrbitaCachePolicy Analytics = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
    public static readonly OrbitaCachePolicy Reference = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));
}

public interface IOrbitaQueryCache
{
    Task<T> GetOrCreateAsync<T>(
        OrbitaCacheDomain domain,
        Guid? officeId,
        string? audience,
        object? parameters,
        OrbitaCachePolicy policy,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a large DTO from Redis directly while retaining the same versioned
    /// keys and cache-aside fallback policy as the HybridCache path.
    /// </summary>
    Task<T> GetOrCreateDistributedAsync<T>(
        OrbitaCacheDomain domain,
        Guid? officeId,
        string? audience,
        object? parameters,
        OrbitaCachePolicy policy,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default);

    Task InvalidateAsync(IReadOnlyList<PanelChangeKind> changes, Guid? officeId);
    void ClearLocalVersion(OrbitaCacheDomain domain, Guid? officeId);
}

/// <summary>
/// Cache-aside facade for read DTOs. The version is read from Redis and becomes
/// part of every data key; invalidation can therefore safely leave old values to
/// expire. If Redis is unavailable this service bypasses both cache tiers.
/// </summary>
public sealed class OrbitaQueryCache(
    HybridCache cache,
    IMemoryCache memoryCache,
    IServiceProvider serviceProvider,
    IOptions<OrbitaCacheOptions> options,
    ILogger<OrbitaQueryCache> logger) : IOrbitaQueryCache
{
    private const string VersionPrefix = "cache-version:v1:";
    private const string LocalVersionPrefix = "orbita-cache-version:";
    private const string InvalidationChannel = "orbita:cache:invalidate:v1";
    private const string IncrementVersionScript = "local version = redis.call('INCR', KEYS[1]); redis.call('EXPIRE', KEYS[1], ARGV[1]); return version";
    private static readonly TimeSpan VersionTtl = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions KeyJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter CacheMeter = new("Orbita.Api.Cache", "1.0");
    private static readonly Counter<long> CacheRequests = CacheMeter.CreateCounter<long>("orbita.cache.requests");
    private static readonly Counter<long> CacheMisses = CacheMeter.CreateCounter<long>("orbita.cache.misses");
    private static readonly Counter<long> CacheBypasses = CacheMeter.CreateCounter<long>("orbita.cache.bypasses");
    private static readonly Counter<long> CacheFallbacks = CacheMeter.CreateCounter<long>("orbita.cache.fallbacks");
    private static readonly Counter<long> CacheInvalidations = CacheMeter.CreateCounter<long>("orbita.cache.invalidations");
    private static readonly TimeSpan RedisFailureBackoff = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, byte> _loggedRedisFailures = new(StringComparer.Ordinal);
    private long _redisUnavailableUntilTicks;

    public async Task<T> GetOrCreateAsync<T>(
        OrbitaCacheDomain domain,
        Guid? officeId,
        string? audience,
        object? parameters,
        OrbitaCachePolicy policy,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        CacheRequests.Add(1, DomainTag(domain));
        if (!IsEnabled(domain) || !TryGetConnectedMultiplexer(out var multiplexer))
        {
            CacheBypasses.Add(1, DomainTag(domain));
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        var version = await GetVersionAsync(multiplexer, domain, officeId, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            CacheBypasses.Add(1, DomainTag(domain));
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        var key = BuildDataKey(domain, officeId, audience, version.Value, parameters);
        var entryOptions = new HybridCacheEntryOptions
        {
            Expiration = policy.DistributedTtl,
            LocalCacheExpiration = policy.LocalTtl,
            Flags = policy.UseLocalCache
                ? HybridCacheEntryFlags.None
                : HybridCacheEntryFlags.DisableLocalCache
        };
        var factoryStarted = 0;

        try
        {
            return await cache.GetOrCreateAsync(
                key,
                async token =>
                {
                    Interlocked.Exchange(ref factoryStarted, 1);
                    CacheMisses.Add(1, DomainTag(domain));
                    return await factory(token).ConfigureAwait(false);
                },
                entryOptions,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch when (Volatile.Read(ref factoryStarted) != 0)
        {
            // The source query failed. Do not disguise that error as a cache miss
            // and do not execute a non-idempotent query factory twice.
            throw;
        }
        catch (Exception exception)
        {
            LogRedisFailure(domain, exception);
            CacheFallbacks.Add(1, DomainTag(domain));
            return await factory(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<T> GetOrCreateDistributedAsync<T>(
        OrbitaCacheDomain domain,
        Guid? officeId,
        string? audience,
        object? parameters,
        OrbitaCachePolicy policy,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        CacheRequests.Add(1, DomainTag(domain));
        if (!IsEnabled(domain)
            || !TryGetConnectedMultiplexer(out var multiplexer)
            || serviceProvider.GetService<IDistributedCache>() is not { } distributedCache)
        {
            CacheBypasses.Add(1, DomainTag(domain));
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        var version = await GetVersionAsync(multiplexer, domain, officeId, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            CacheBypasses.Add(1, DomainTag(domain));
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        var key = BuildDataKey(domain, officeId, audience, version.Value, parameters);
        try
        {
            var payload = await distributedCache.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (payload is { Length: > 0 })
            {
                var cached = JsonSerializer.Deserialize<T>(payload, PayloadJsonOptions);
                if (cached is not null)
                {
                    return cached;
                }

                await distributedCache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            LogRedisFailure(domain, exception);
            CacheFallbacks.Add(1, DomainTag(domain));
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        CacheMisses.Add(1, DomainTag(domain));
        var value = await factory(cancellationToken).ConfigureAwait(false);
        try
        {
            await distributedCache.SetAsync(
                key,
                JsonSerializer.SerializeToUtf8Bytes(value, PayloadJsonOptions),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = policy.DistributedTtl
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogRedisFailure(domain, exception);
            CacheFallbacks.Add(1, DomainTag(domain));
        }

        return value;
    }

    public async Task InvalidateAsync(IReadOnlyList<PanelChangeKind> changes, Guid? officeId)
    {
        if (!options.Value.Enabled || !TryGetConnectedMultiplexer(out var multiplexer))
        {
            return;
        }

        foreach (var domain in MapDomains(changes))
        {
            // Global-admin queries have a distinct key, but include every office.
            // Invalidate it alongside the changed office to avoid a stale global view.
            var targets = officeId is Guid resolvedOfficeId
                ? new Guid?[] { resolvedOfficeId, null }
                : new Guid?[] { null };
            foreach (var targetOfficeId in targets)
            {
                try
                {
                    var version = (long)await multiplexer.GetDatabase()
                        .ScriptEvaluateAsync(
                            IncrementVersionScript,
                            [VersionKey(domain, targetOfficeId)],
                            [(long)VersionTtl.TotalSeconds])
                        .ConfigureAwait(false);
                    CacheInvalidations.Add(1, DomainTag(domain));
                    ClearLocalVersion(domain, targetOfficeId);
                    var message = JsonSerializer.Serialize(new CacheInvalidation(domain, targetOfficeId, version));
                    await multiplexer.GetSubscriber().PublishAsync(RedisChannel.Literal(InvalidationChannel), message).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    LogRedisFailure(domain, exception);
                }
            }
        }
    }

    public void ClearLocalVersion(OrbitaCacheDomain domain, Guid? officeId) =>
        memoryCache.Remove(LocalVersionKey(domain, officeId));

    internal static IReadOnlyList<OrbitaCacheDomain> MapDomains(IReadOnlyList<PanelChangeKind> changes) =>
        changes
            .SelectMany(static change => (IEnumerable<OrbitaCacheDomain>)(change switch
            {
                PanelChangeKind.Crm => new[] { OrbitaCacheDomain.Crm, OrbitaCacheDomain.Responses, OrbitaCacheDomain.Analytics },
                PanelChangeKind.Responses => new[] { OrbitaCacheDomain.Responses, OrbitaCacheDomain.Dashboard, OrbitaCacheDomain.Analytics },
                PanelChangeKind.Statistics => new[] { OrbitaCacheDomain.Dashboard, OrbitaCacheDomain.Analytics },
                // Worker heartbeats, telemetry snapshots and event streams are high
                // frequency. Their dashboard fields are allowed to be up to five
                // seconds old, so they rely on the Realtime TTL rather than making
                // every in-flight summary cache miss. Meaningful writes include
                // Dashboard explicitly and still invalidate immediately.
                PanelChangeKind.Dashboard => new[] { OrbitaCacheDomain.Dashboard },
                // Heartbeats, account telemetry, response ingestion and event
                // streams are all high frequency. Worker details rely on their
                // five-second TTL for those live counters. Dedicated worker-detail
                // writes can invalidate immediately without evicting this hot path.
                PanelChangeKind.WorkerDetails => new[] { OrbitaCacheDomain.WorkerDetails },
                PanelChangeKind.Reference => new[] { OrbitaCacheDomain.Reference },
                _ => Array.Empty<OrbitaCacheDomain>()
            }))
            .Distinct()
            .ToArray();

    internal static string BuildDataKey(
        OrbitaCacheDomain domain,
        Guid? officeId,
        string? audience,
        long version,
        object? parameters)
    {
        var serialized = JsonSerializer.Serialize(parameters, KeyJsonOptions);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized))).ToLowerInvariant();
        var office = officeId?.ToString("N") ?? "global";
        var access = string.IsNullOrWhiteSpace(audience)
            ? "shared"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(audience))).ToLowerInvariant()[..16];
        return $"orbita:v1:{domain.ToString().ToLowerInvariant()}:{office}:{access}:v{version}:{digest}";
    }

    internal static string VersionKey(OrbitaCacheDomain domain, Guid? officeId) =>
        $"{VersionPrefix}{domain.ToString().ToLowerInvariant()}:{officeId?.ToString("N") ?? "global"}";

    internal static string LocalVersionKey(OrbitaCacheDomain domain, Guid? officeId) =>
        $"{LocalVersionPrefix}{VersionKey(domain, officeId)}";

    private async Task<long?> GetVersionAsync(
        IConnectionMultiplexer multiplexer,
        OrbitaCacheDomain domain,
        Guid? officeId,
        CancellationToken cancellationToken)
    {
        var localKey = LocalVersionKey(domain, officeId);
        if (memoryCache.TryGetValue(localKey, out long cached))
        {
            return cached;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var database = multiplexer.GetDatabase();
            var value = await database.StringGetAsync(VersionKey(domain, officeId)).ConfigureAwait(false);
            var version = value.HasValue && long.TryParse(value.ToString(), out var parsed) ? parsed : 0L;
            memoryCache.Set(localKey, version, TimeSpan.FromSeconds(1));
            Interlocked.Exchange(ref _redisUnavailableUntilTicks, 0);
            return version;
        }
        catch (Exception exception)
        {
            LogRedisFailure(domain, exception);
            OpenCircuit();
            return null;
        }
    }

    private bool IsEnabled(OrbitaCacheDomain domain)
    {
        var value = options.Value;
        if (!value.Enabled)
        {
            return false;
        }

        return domain switch
        {
            OrbitaCacheDomain.Dashboard => value.Domains.Dashboard,
            OrbitaCacheDomain.WorkerDetails => value.Domains.WorkerDetails,
            OrbitaCacheDomain.Responses => value.Domains.Responses,
            OrbitaCacheDomain.Crm => value.Domains.Crm,
            OrbitaCacheDomain.Analytics => value.Domains.Analytics,
            OrbitaCacheDomain.Reference => value.Domains.Reference,
            OrbitaCacheDomain.Logs => value.Domains.Logs,
            _ => false
        };
    }

    private IConnectionMultiplexer? GetMultiplexer() =>
        serviceProvider.GetService<IConnectionMultiplexer>();

    private bool TryGetConnectedMultiplexer(out IConnectionMultiplexer multiplexer)
    {
        multiplexer = null!;
        if (DateTime.UtcNow.Ticks < Volatile.Read(ref _redisUnavailableUntilTicks)
            || GetMultiplexer() is not { IsConnected: true } connected)
        {
            return false;
        }

        multiplexer = connected;
        return true;
    }

    private void OpenCircuit() =>
        Interlocked.Exchange(ref _redisUnavailableUntilTicks, (DateTime.UtcNow + RedisFailureBackoff).Ticks);

    private static KeyValuePair<string, object?> DomainTag(OrbitaCacheDomain domain) =>
        new("domain", domain.ToString().ToLowerInvariant());

    private void LogRedisFailure(OrbitaCacheDomain domain, Exception exception)
    {
        OpenCircuit();
        if (_loggedRedisFailures.TryAdd(domain.ToString(), 0))
        {
            logger.LogWarning(exception, "Redis cache is unavailable for {CacheDomain}; queries will use PostgreSQL.", domain);
        }
    }

    internal sealed record CacheInvalidation(OrbitaCacheDomain Domain, Guid? OfficeId, long Version);

    internal const string RedisInvalidationChannel = InvalidationChannel;
}

public sealed class RedisCacheInvalidationListener(
    IServiceProvider serviceProvider,
    IOrbitaQueryCache queryCache,
    IOptions<OrbitaCacheOptions> options,
    ILogger<RedisCacheInvalidationListener> logger) : IHostedService
{
    private ISubscriber? _subscriber;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || serviceProvider.GetService<IConnectionMultiplexer>() is not { IsConnected: true } multiplexer)
        {
            return;
        }

        _subscriber = multiplexer.GetSubscriber();
        await _subscriber.SubscribeAsync(RedisChannel.Literal(OrbitaQueryCache.RedisInvalidationChannel), (_, payload) =>
        {
            try
            {
                var message = JsonSerializer.Deserialize<OrbitaQueryCache.CacheInvalidation>(payload.ToString());
                if (message is not null)
                {
                    queryCache.ClearLocalVersion(message.Domain, message.OfficeId);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not process Redis cache invalidation.");
            }
        }).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscriber is not null)
        {
            await _subscriber.UnsubscribeAsync(RedisChannel.Literal(OrbitaQueryCache.RedisInvalidationChannel)).ConfigureAwait(false);
        }
    }
}
