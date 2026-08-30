using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Multilogin;
using LeadFlow.Core.Services.Worker;

namespace Orbita.Tests;

public sealed class BrowserProviderConnectionProbeTests
{
    [Fact]
    public async Task CheckAdsPowerAsync_Success_ReturnsProfileAndGroupCounts()
    {
        var ads = new FakeAdsPowerApi
        {
            Profiles =
            [
                new AdsPowerProfileSummary("u1", "one", null, "A", "g1"),
                new AdsPowerProfileSummary("u2", "two", null, "A", "g1")
            ],
            Groups = [new AdsPowerGroupSummary("g1", "A"), new AdsPowerGroupSummary("g2", "B")]
        };
        var sut = new BrowserProviderConnectionProbe(ads, new FakeMultiloginApi());

        var result = await sut.CheckAdsPowerAsync(new AdsPowerConnectionOptions("http://local.adspower.net:50325", "key"));

        Assert.True(result.Success);
        Assert.Equal(2, result.ProfileCount);
        Assert.Equal(2, result.GroupCount);
        Assert.Equal(2, result.Groups!.Count);
        Assert.Contains("профилей", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("key", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAdsPowerAsync_ApiError_ReturnsFailureWithoutSecret()
    {
        var ads = new FakeAdsPowerApi
        {
            ProfilesError = new InvalidOperationException("AdsPower key=super-secret-key denied")
        };
        var sut = new BrowserProviderConnectionProbe(ads, new FakeMultiloginApi());

        var result = await sut.CheckAdsPowerAsync(
            new AdsPowerConnectionOptions("http://local.adspower.net:50325", "super-secret-key"));

        Assert.False(result.Success);
        Assert.DoesNotContain("super-secret-key", result.Message, StringComparison.Ordinal);
        Assert.Null(result.ProfileCount);
    }

    [Fact]
    public async Task CheckMultiloginAsync_WithoutToken_NeedsSetup()
    {
        var mlx = new FakeMultiloginApi();
        var sut = new BrowserProviderConnectionProbe(new FakeAdsPowerApi(), mlx);
        var result = await sut.CheckMultiloginAsync(new MultiloginConnectionOptions());
        Assert.False(result.Success);
        Assert.Equal("Укажите API Token Multilogin.", result.Message);
        Assert.Equal(0, mlx.ProbeCount);
        Assert.Equal(0, mlx.SearchCount);
    }

    [Fact]
    public async Task CheckMultiloginAsync_LauncherDown_DoesNotSearchProfiles()
    {
        var mlx = new FakeMultiloginApi
        {
            ProbeError = new InvalidOperationException("Launcher Multilogin недоступен.")
        };
        var sut = new BrowserProviderConnectionProbe(new FakeAdsPowerApi(), mlx);

        var result = await sut.CheckMultiloginAsync(new MultiloginConnectionOptions { AutomationToken = "mlx-token-value" });

        Assert.False(result.Success);
        Assert.Equal(1, mlx.ProbeCount);
        Assert.Equal(0, mlx.SearchCount);
        Assert.Contains("Launcher Multilogin недоступен", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mlx-token-value", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckMultiloginAsync_Success_ReturnsProfileCount()
    {
        var mlx = new FakeMultiloginApi
        {
            Catalog = new MultiloginProfileSearchResult(
                [new MultiloginProfileSummary("p1", "f1", "one")],
                IsComplete: true)
        };
        var sut = new BrowserProviderConnectionProbe(new FakeAdsPowerApi(), mlx);

        var result = await sut.CheckMultiloginAsync(new MultiloginConnectionOptions { AutomationToken = "mlx-token-value" });

        Assert.True(result.Success);
        Assert.Equal(1, result.ProfileCount);
        Assert.Equal(1, mlx.ProbeCount);
        Assert.Equal(1, mlx.SearchCount);
        Assert.DoesNotContain("mlx-token-value", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckLocalChrome_ExistingFile_Succeeds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chrome-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0]);
        try
        {
            var sut = new BrowserProviderConnectionProbe(new FakeAdsPowerApi(), new FakeMultiloginApi());
            var result = sut.CheckLocalChrome(path);
            Assert.True(result.Success);
            Assert.Equal(path, result.ResolvedExecutablePath);
            Assert.Contains("Найден браузер", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CheckLocalChrome_MissingFile_Fails()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-chrome-{Guid.NewGuid():N}.exe");
        var sut = new BrowserProviderConnectionProbe(new FakeAdsPowerApi(), new FakeMultiloginApi());
        var result = sut.CheckLocalChrome(missing);
        Assert.False(result.Success);
        Assert.Contains("не найден", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.ResolvedExecutablePath);
    }

    private sealed class FakeAdsPowerApi : IAdsPowerApiClient
    {
        public IReadOnlyList<AdsPowerProfileSummary> Profiles { get; init; } = [];
        public IReadOnlyList<AdsPowerGroupSummary> Groups { get; init; } = [];
        public Exception? ProfilesError { get; init; }
        public Exception? GroupsError { get; init; }

        public Task<IReadOnlyList<AdsPowerProfileSummary>> ListProfilesAsync(
            AdsPowerConnectionOptions options,
            CancellationToken cancellationToken = default,
            string? groupId = null)
        {
            _ = options;
            _ = groupId;
            cancellationToken.ThrowIfCancellationRequested();
            if (ProfilesError is not null)
            {
                throw ProfilesError;
            }

            return Task.FromResult(Profiles);
        }

        public Task<IReadOnlyList<AdsPowerGroupSummary>> ListGroupsAsync(
            AdsPowerConnectionOptions options,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            cancellationToken.ThrowIfCancellationRequested();
            if (GroupsError is not null)
            {
                throw GroupsError;
            }

            return Task.FromResult(Groups);
        }

        public Task<AdsPowerProfileProxy?> GetProfileProxyAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdsPowerBrowserStartResult> StartBrowserAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            string? openUrl,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopBrowserAsync(
            AdsPowerConnectionOptions options,
            string adsPowerUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMultiloginApi : IMultiloginApiClient
    {
        public MultiloginProfileSearchResult Catalog { get; init; } = MultiloginProfileSearchResult.EmptyComplete;
        public Exception? ProbeError { get; init; }
        public Exception? SearchError { get; init; }
        public int ProbeCount { get; private set; }
        public int SearchCount { get; private set; }

        public Task ProbeLauncherAsync(
            MultiloginConnectionOptions options,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCount++;
            if (ProbeError is not null)
            {
                throw ProbeError;
            }

            return Task.CompletedTask;
        }

        public Task<MultiloginProfileSearchResult> SearchProfilesAsync(
            MultiloginConnectionOptions options,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            cancellationToken.ThrowIfCancellationRequested();
            SearchCount++;
            if (SearchError is not null)
            {
                throw SearchError;
            }

            return Task.FromResult(Catalog);
        }

        public Task<MultiloginBrowserStartResult> StartProfileAsync(
            MultiloginConnectionOptions options,
            string folderId,
            string profileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopProfileAsync(
            MultiloginConnectionOptions options,
            string profileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
