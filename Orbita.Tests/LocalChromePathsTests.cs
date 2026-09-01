using LeadFlow.Core.Services.LocalChrome;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class LocalChromePathsTests
{
    [Fact]
    public void NormalizeUserDataDir_RejectsEmpty()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LocalChromePaths.NormalizeUserDataDir("  "));
        Assert.Contains("отдельной папке профиля", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeUserDataDir_RejectsDefaultChromeProfile()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(local, "Google", "Chrome", "User Data");
        var ex = Assert.Throws<InvalidOperationException>(() => LocalChromePaths.NormalizeUserDataDir(path));
        Assert.Contains("стандартный профиль Chrome", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeUserDataDir_RejectsTooLong()
    {
        var path = new string('a', LocalChromePaths.MaxUserDataDirLength + 1);
        var ex = Assert.Throws<InvalidOperationException>(() => LocalChromePaths.NormalizeUserDataDir(path));
        Assert.Contains("1024", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeUserDataDir_AcceptsDedicatedFolder()
    {
        var path = @"D:\Orbita\ChromeProfiles\acc-1";
        Assert.Equal(path, LocalChromePaths.NormalizeUserDataDir("  " + path + "  "));
        Assert.False(LocalChromePaths.IsDefaultBrowserProfile(path));
    }

    [Fact]
    public void GetManagedUserDataDir_IsUniqueAndNotDefault()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var path1 = LocalChromePaths.GetManagedUserDataDir(first);
        var path2 = LocalChromePaths.GetManagedUserDataDir(second);

        Assert.NotEqual(path1, path2);
        Assert.Contains(first.ToString("D"), path1, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Orbita", path1, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ChromeProfiles", path1, StringComparison.OrdinalIgnoreCase);
        Assert.False(LocalChromePaths.IsDefaultBrowserProfile(path1));
        Assert.False(path1.EndsWith($"{Path.DirectorySeparatorChar}Default", StringComparison.OrdinalIgnoreCase));
        Assert.False(path1.Contains($"{Path.DirectorySeparatorChar}Google{Path.DirectorySeparatorChar}Chrome{Path.DirectorySeparatorChar}User Data", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizeUserDataDir_ExpandsManagedMarker_WithoutUsingDefault()
    {
        var accountId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var marker = LocalChromeProfileMarkers.CreateManaged(accountId);
        var resolved = LocalChromePaths.NormalizeUserDataDir(marker, accountId);

        Assert.Equal(LocalChromePaths.GetManagedUserDataDir(accountId), resolved);
        Assert.False(LocalChromePaths.IsDefaultBrowserProfile(resolved));
        Assert.False(LocalChromeProfileMarkers.IsManaged(resolved));
    }

    [Fact]
    public void NormalizeUserDataDir_KeepsExplicitExistingPath()
    {
        var path = @"D:\Orbita\ChromeProfiles\acc-1";
        Assert.Equal(path, LocalChromePaths.NormalizeUserDataDir("  " + path + "  "));
    }

    [Fact]
    public void IsDefaultBrowserProfile_RejectsDefaultFolderName()
    {
        Assert.True(LocalChromePaths.IsDefaultBrowserProfile(@"D:\Chrome\User Data\Default"));
        Assert.True(LocalChromeUserDataRules.LooksLikeForbiddenProfile(@"C:\Users\user\AppData\Local\Google\Chrome\User Data"));
    }

    [Fact]
    public void ResolveExecutable_MissingConfiguredFile_ThrowsRussian()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-chrome-{Guid.NewGuid():N}.exe");
        var ex = Assert.Throws<InvalidOperationException>(() => LocalChromePaths.ResolveExecutable(missing));
        Assert.Contains("Файл браузера не найден", ex.Message, StringComparison.Ordinal);
        Assert.Contains("настройках воркера", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveExecutable_ExistingFile_ReturnsTrimmedPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chrome-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0]);
        try
        {
            Assert.Equal(path, LocalChromePaths.ResolveExecutable("  " + path + "  "));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EnsureUserDataDir_CreatesMissingFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orbita-chrome-{Guid.NewGuid():N}");
        try
        {
            Assert.False(Directory.Exists(path));
            LocalChromePaths.EnsureUserDataDir(path);
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
