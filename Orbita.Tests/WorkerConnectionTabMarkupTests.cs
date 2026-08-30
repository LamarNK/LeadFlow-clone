namespace Orbita.Tests;

public sealed class WorkerConnectionTabMarkupTests
{
    [Fact]
    public void ConnectionTab_UsesCompactSourceRows_NotLargeCards()
    {
        var view = ReadRepoFile("Orbita.Web/Views/Workers/Details.cshtml");

        Assert.DoesNotContain("worker-provider-cards", view);
        Assert.DoesNotContain("worker-provider-card__", view);
        Assert.DoesNotContain("class=\"worker-provider-card\"", view);
        Assert.Contains("data-provider-sources", view);
        Assert.Contains("worker-provider-row", view);
        Assert.Contains("Источники браузеров", view);
        Assert.Contains("data-provider-card=", view);
        Assert.Contains("data-provider-check", view);
        Assert.Contains("data-provider-sync", view);
        Assert.Contains("data-provider-toggle", view);
        Assert.Contains("data-provider-section=\"AdsPower\"", view);
        Assert.Contains("data-provider-section=\"Multilogin\"", view);
        Assert.Contains("data-provider-section=\"Local\"", view);
        Assert.Contains("RuCaptcha", view);
        Assert.DoesNotContain("AdsPower Local API", view);
        Assert.DoesNotContain("Проверить подключение", view);
        Assert.DoesNotContain("Синхронизировать сейчас", view);
        Assert.Matches(@">\s*Проверить\s*<", view);
        Assert.Matches(@">\s*Синхронизировать\s*<", view);
        Assert.DoesNotContain("<span>Использовать AdsPower</span>", view);
        Assert.DoesNotContain("class=\"worker-provider-toggle-label\"", view);
    }

    [Fact]
    public void ConnectionTabCss_DropsCardGridAndStickyOverlay()
    {
        var css = ReadRepoFile("Orbita.Web/wwwroot/css/orbita/workers.css");

        Assert.DoesNotContain(".worker-provider-cards", css);
        Assert.DoesNotContain(".worker-provider-card {", css);
        Assert.Contains(".worker-provider-row", css);
        Assert.Contains(".worker-provider-status--checking::before", css);
        Assert.Contains(".worker-settings-actions", css);
        Assert.DoesNotContain("position: sticky;\n    bottom:", css.Replace("\r\n", "\n"));
        Assert.DoesNotContain("box-shadow: 0 8px 24px rgba(16, 24, 40, 0.08)", css);
        Assert.DoesNotMatch(@"\.worker-settings-section-title\s*\{[^}]*text-transform:\s*uppercase", css);
        Assert.DoesNotMatch(@"\.worker-settings-subsection-title\s*\{[^}]*text-transform:\s*uppercase", css);
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
