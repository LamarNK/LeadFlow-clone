using Microsoft.Extensions.Logging.Abstractions;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class ResponseCacheInvalidatorTests
{
    [Fact]
    public async Task Batch_InvalidatesEachOfficeOnceAfterOuterBatchCompletes()
    {
        var cache = new RecordingQueryCache();
        var invalidator = new ResponseCacheInvalidator(
            cache,
            NullLogger<ResponseCacheInvalidator>.Instance);
        var firstOffice = Guid.NewGuid();
        var secondOffice = Guid.NewGuid();

        await using (invalidator.BeginBatch())
        {
            await invalidator.InvalidateAsync(firstOffice);
            await using (invalidator.BeginBatch())
            {
                await invalidator.InvalidateAsync(firstOffice);
                await invalidator.InvalidateAsync(secondOffice);
            }
            Assert.Empty(cache.InvalidatedOfficeIds);
        }

        Assert.Equal(2, cache.InvalidatedOfficeIds.Count);
        Assert.Contains(firstOffice, cache.InvalidatedOfficeIds);
        Assert.Contains(secondOffice, cache.InvalidatedOfficeIds);
        Assert.All(cache.Changes, changes => Assert.Contains(PanelChangeKind.Responses, changes));
    }

    [Fact]
    public async Task RedisFailure_DoesNotFailSuccessfulWritePath()
    {
        var invalidator = new ResponseCacheInvalidator(
            new ThrowingQueryCache(),
            NullLogger<ResponseCacheInvalidator>.Instance);

        await invalidator.InvalidateAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task FlushAsync_InvalidatesBeforeScopeIsDisposed()
    {
        var cache = new RecordingQueryCache();
        var invalidator = new ResponseCacheInvalidator(
            cache,
            NullLogger<ResponseCacheInvalidator>.Instance);
        var officeId = Guid.NewGuid();

        await using var batch = invalidator.BeginBatch();
        await invalidator.InvalidateAsync(officeId);
        await batch.FlushAsync();

        Assert.Contains(officeId, cache.InvalidatedOfficeIds);
    }

    private class RecordingQueryCache : IOrbitaQueryCache
    {
        public List<Guid?> InvalidatedOfficeIds { get; } = [];
        public List<IReadOnlyList<PanelChangeKind>> Changes { get; } = [];

        public virtual Task InvalidateAsync(IReadOnlyList<PanelChangeKind> changes, Guid? officeId)
        {
            InvalidatedOfficeIds.Add(officeId);
            Changes.Add(changes);
            return Task.CompletedTask;
        }

        public Task<T> GetOrCreateAsync<T>(OrbitaCacheDomain domain, Guid? officeId, string? audience,
            object? parameters, OrbitaCachePolicy policy, Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default) => factory(cancellationToken);

        public Task<T> GetOrCreateDistributedAsync<T>(OrbitaCacheDomain domain, Guid? officeId, string? audience,
            object? parameters, OrbitaCachePolicy policy, Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default) => factory(cancellationToken);

        public void ClearLocalVersion(OrbitaCacheDomain domain, Guid? officeId) { }
    }

    private sealed class ThrowingQueryCache : RecordingQueryCache
    {
        public override Task InvalidateAsync(IReadOnlyList<PanelChangeKind> changes, Guid? officeId) =>
            Task.FromException(new InvalidOperationException("Redis unavailable"));
    }
}
