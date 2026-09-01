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
        Assert.Contains("data-traffic-mode", view);
        Assert.Contains("data-block-media", view);

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
        Assert.Contains("Скорость и трафик", js);
        Assert.Contains("Обычный", js);
        Assert.Contains("Экономный", js);
        Assert.Contains("Агрессивный", js);
        Assert.Contains("Пользовательский", js);
        Assert.Contains("Блокировать видео и аудио", js);
        Assert.Contains("Блокировать внешнюю аналитику", js);
        Assert.Contains("Не загружать изображения карточек", js);
        Assert.Contains("Не загружать веб-шрифты", js);
        Assert.Contains("Отключить предзагрузку страниц", js);
        Assert.Contains("Кэш профиля включён всегда: повторные заходы быстрее", js);
        Assert.Contains("Настройки применяются только во время мониторинга", js);
        Assert.Contains("Открыть браузер", js);
        Assert.Contains("type=\"password\"", js);
        Assert.DoesNotContain("SOCKS5", js);
    }

    [Fact]
    public void WorkerDetails_DoesNotShowTrafficSettingsOutsideLocalBranch()
    {
        var view = ReadRepoFile("Orbita.Web/Views/Workers/Details.cshtml");
        var stripped = StripLocalBlocks(view);
        Assert.DoesNotContain("data-local-profile-settings", stripped);
        Assert.DoesNotContain("data-traffic-mode", stripped);
        Assert.DoesNotContain("data-block-media", stripped);
        Assert.DoesNotContain("Скорость и трафик", stripped);
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
