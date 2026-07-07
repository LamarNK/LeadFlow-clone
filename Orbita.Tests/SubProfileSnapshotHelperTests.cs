using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class SubProfileSnapshotHelperTests
{
    private const string ExistingJson = """
        [
          {"Id":"sp-1","Name":"Alpha","Balance":4200,"WalletBalance":100},
          {"Id":"sp-2","Name":"Beta","Balance":800,"WalletBalance":0}
        ]
        """;

    [Fact]
    public void ShouldPersistSubProfiles_ReturnsFalse_ForEmptyIncoming()
    {
        Assert.False(SubProfileSnapshotHelper.ShouldPersistSubProfiles([], ExistingJson));
    }

    [Fact]
    public void ShouldPersistSubProfiles_ReturnsFalse_ForPlaceholderIncoming()
    {
        var incoming = new[] { new WorkerSubProfileDto("—", "—", "", false, null, null, null, null) };

        Assert.False(SubProfileSnapshotHelper.ShouldPersistSubProfiles(incoming, ExistingJson));
    }

    [Fact]
    public void ShouldPersistSubProfiles_ReturnsFalse_WhenIncomingHasFewerProfiles()
    {
        var incoming = new[] { new WorkerSubProfileDto("sp-1", "Alpha", "", true, 100m, null, null, null) };

        Assert.False(SubProfileSnapshotHelper.ShouldPersistSubProfiles(incoming, ExistingJson));
    }

    [Fact]
    public void ShouldPersistSubProfiles_ReturnsFalse_WhenIncomingStripsBalances()
    {
        var incoming = new[]
        {
            new WorkerSubProfileDto("sp-1", "Alpha", "", true, null, null, null, null),
            new WorkerSubProfileDto("sp-2", "Beta", "", false, null, null, null, null)
        };

        Assert.False(SubProfileSnapshotHelper.ShouldPersistSubProfiles(incoming, ExistingJson));
    }

    [Fact]
    public void MergeForPersist_ReturnsNull_WhenIncomingIsPlaceholderOnly()
    {
        var incoming = new[] { new WorkerSubProfileDto("", "—", "", false, null, null, null, null) };

        Assert.Null(SubProfileSnapshotHelper.MergeForPersist(incoming, ExistingJson));
    }

    [Fact]
    public void MergeForPersist_PreservesBalances_WhenIncomingUpdatesWithoutBalances()
    {
        var incoming = new[]
        {
            new WorkerSubProfileDto("sp-1", "Alpha", "Работа", true, null, null, null, null),
            new WorkerSubProfileDto("sp-2", "Beta", "Работа", false, null, null, null, null),
            new WorkerSubProfileDto("sp-3", "Gamma", "Работа", false, 500m, null, null, null)
        };
        var existing = """
            [
              {"Id":"sp-1","Name":"Alpha","Balance":4200,"WalletBalance":100},
              {"Id":"sp-2","Name":"Beta","Balance":800,"WalletBalance":0}
            ]
            """;

        var merged = SubProfileSnapshotHelper.MergeForPersist(incoming, existing);

        Assert.NotNull(merged);
        Assert.Equal(3, merged!.Count);
        Assert.Equal(4200m, merged[0].Balance);
        Assert.Equal(100m, merged[0].WalletBalance);
        Assert.Equal(800m, merged[1].Balance);
        Assert.Equal(500m, merged[2].Balance);
    }

    [Fact]
    public void MergeForPersist_AllowsFirstMeaningfulWrite_WhenExistingEmpty()
    {
        var incoming = new[]
        {
            new WorkerSubProfileDto("438814802", "Работа вахтой2", "Работа", true, null, null, null, null)
        };

        var merged = SubProfileSnapshotHelper.MergeForPersist(incoming, "[]");

        Assert.NotNull(merged);
        Assert.Equal("438814802", Assert.Single(merged!).Id);
    }
}