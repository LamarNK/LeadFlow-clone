namespace Orbita.Tests;

public sealed class LocalChromeProfileSettingsMarkupTests
{
    [Fact]
    public void WorkerDetails_ShowsProfileSettingsOnlyForLocalAccounts()
    {
        var view = ReadRepoFile("Orbita.Web/Views/Workers/Details.cshtml");

        Assert.Contains("data-local-profile-settings", view);
        Assert.Contains("Настройки профиля", view);
        Assert.Contains("worker-local-settings-btn", view);

        var localBlockIndex = view.IndexOf("@if (account.IsLocal)", StringComparison.Ordinal);
        Assert.True(localBlockIndex >= 0);
        var settingsIndex = view.IndexOf("data-local-profile-settings", localBlockIndex, StringComparison.Ordinal);
        Assert.True(settingsIndex > localBlockIndex);

        var adsCredentials = view.IndexOf("data-avito-credentials", StringComparison.Ordinal);
        Assert.True(adsCredentials > 0);
    }

    [Fact]
    public void WorkerDetails_DoesNotAddProfileSettingsOutsideLocalBranch()
    {
        var view = ReadRepoFile("Orbita.Web/Views/Workers/Details.cshtml");
        Assert.DoesNotContain("data-local-profile-settings", StripLocalBlocks(view));
    }

    [Fact]
    public void WorkerJs_BindsSettingsOnlyViaLocalAttribute()
    {
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita/workers.js");
        Assert.Contains("data-local-profile-settings", js);
        Assert.Contains("Настройки профиля", js);
        Assert.Contains("Прокси", js);
        Assert.Contains("Открыть браузер", js);
        Assert.Contains("type=\"password\"", js);
        Assert.DoesNotContain("SOCKS5", js);
    }

    private static string StripLocalBlocks(string view)
    {
        while (true)
        {
            var start = view.IndexOf("@if (account.IsLocal)", StringComparison.Ordinal);
            if (start < 0)
            {
                return view;
            }

            var open = view.IndexOf('{', start);
            if (open < 0)
            {
                return view;
            }

            var depth = 0;
            var i = open;
            for (; i < view.Length; i++)
            {
                if (view[i] == '{')
                {
                    depth++;
                }
                else if (view[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        i++;
                        break;
                    }
                }
            }

            view = view.Remove(start, i - start);
        }
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
