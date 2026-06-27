using Orbita.Api.Helpers;

namespace Orbita.Tests;

public sealed class AppVersionHelperTests
{
    [Theory]
    [InlineData("1.0.0.2", "1.0.0.1", true)]
    [InlineData("1.0.0.1", "1.0.0.1", false)]
    [InlineData("1.0.0.0", "1.0.0.1", false)]
    [InlineData("2.0.0.0", "1.9.9.9", true)]
    public void IsNewer_ComparesFourPartVersions(string latest, string current, bool expected) =>
        Assert.Equal(expected, AppVersionHelper.IsNewer(latest, current));

    [Theory]
    [InlineData("Orbita.Worker.Setup-1.0.0.5.msi", "1.0.0.5")]
    [InlineData("orbita.worker.setup-2.1.0.0.msi", "2.1.0.0")]
    public void TryParseVersionFromFileName_ParsesSetupName(string fileName, string expectedVersion)
    {
        var parsed = AppVersionHelper.TryParseVersionFromFileName(fileName, out var version);
        Assert.True(parsed);
        Assert.Equal(expectedVersion, version);
    }

    [Fact]
    public void GetReleaseDirectoryName_PrefixesVersion() =>
        Assert.Equal("v1.0.0.1", AppVersionHelper.GetReleaseDirectoryName("1.0.0.1"));
}