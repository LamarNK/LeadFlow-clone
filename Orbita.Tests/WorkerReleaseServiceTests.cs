using Microsoft.Extensions.Options;
using Orbita.Api.Options;
using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class WorkerReleaseServiceTests : IDisposable
{
    private readonly string _root;
    private readonly WorkerReleaseService _service;

    public WorkerReleaseServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"orbita-releases-{Guid.NewGuid():N}");
        _service = new WorkerReleaseService(Options.Create(new WorkerReleaseOptions
        {
            DataPath = _root,
            MaxUploadBytes = 1024 * 1024
        }));
    }

    [Fact]
    public async Task Upload_List_CheckUpdate_WorkEndToEnd()
    {
        await using var package = CreateFakeMsiStream();
        var (release, error) = await _service.UploadAsync(
            package,
            "Orbita.Worker.Setup-1.0.0.3.msi",
            version: null,
            releaseNotes: "Test release",
            CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(release);
        Assert.Equal("1.0.0.3", release!.Version);
        Assert.True(release.IsLatest);

        var list = await _service.ListAsync();
        Assert.Single(list.Versions);
        Assert.Equal("1.0.0.3", list.Latest?.Version);

        var checkOld = await _service.CheckUpdateAsync("1.0.0.1");
        Assert.True(checkOld.HasUpdate);
        Assert.Equal("1.0.0.3", checkOld.LatestVersion);

        var checkCurrent = await _service.CheckUpdateAsync("1.0.0.3");
        Assert.False(checkCurrent.HasUpdate);
    }

    [Fact]
    public async Task SetLatest_SwitchesLatestManifest()
    {
        await UploadVersionAsync("1.0.0.1");
        await UploadVersionAsync("1.0.0.2");

        var setResult = await _service.SetLatestAsync("1.0.0.1");
        Assert.True(setResult.Success);

        var latest = await _service.GetLatestAsync();
        Assert.Equal("1.0.0.1", latest?.Version);
    }

    private async Task UploadVersionAsync(string version)
    {
        await using var package = CreateFakeMsiStream();
        var (_, error) = await _service.UploadAsync(
            package,
            $"Orbita.Worker.Setup-{version}.msi",
            version,
            releaseNotes: null,
            CancellationToken.None);
        Assert.Null(error);
    }

    private static MemoryStream CreateFakeMsiStream()
    {
        var bytes = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x01, 0x02 };
        return new MemoryStream(bytes, writable: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}