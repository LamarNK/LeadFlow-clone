using LeadFlow.Core.Services.LocalChrome;

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
