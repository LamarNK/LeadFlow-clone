using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class LocalChromeTrafficRulesTests
{
    [Fact]
    public void Presets_ExposeExpectedFlags()
    {
        Assert.Equal(LocalChromeTrafficRules.ModeNormal, LocalChromeTrafficRules.Normal.Mode);
        Assert.False(LocalChromeTrafficRules.Normal.BlockMedia);
        Assert.False(LocalChromeTrafficRules.Normal.BlockAnalytics);
        Assert.False(LocalChromeTrafficRules.Normal.BlockImages);
        Assert.False(LocalChromeTrafficRules.Normal.BlockFonts);
        Assert.False(LocalChromeTrafficRules.Normal.BlockPrefetch);
        Assert.Equal(60, LocalChromeTrafficRules.Normal.NavigationTimeoutSeconds);

        Assert.True(LocalChromeTrafficRules.Economic.BlockMedia);
        Assert.True(LocalChromeTrafficRules.Economic.BlockAnalytics);
        Assert.False(LocalChromeTrafficRules.Economic.BlockImages);
        Assert.False(LocalChromeTrafficRules.Economic.BlockFonts);
        Assert.True(LocalChromeTrafficRules.Economic.BlockPrefetch);
        Assert.Equal(60, LocalChromeTrafficRules.Economic.NavigationTimeoutSeconds);

        Assert.True(LocalChromeTrafficRules.Aggressive.BlockMedia);
        Assert.True(LocalChromeTrafficRules.Aggressive.BlockAnalytics);
        Assert.True(LocalChromeTrafficRules.Aggressive.BlockImages);
        Assert.True(LocalChromeTrafficRules.Aggressive.BlockFonts);
        Assert.True(LocalChromeTrafficRules.Aggressive.BlockPrefetch);
        Assert.Equal(60, LocalChromeTrafficRules.Aggressive.NavigationTimeoutSeconds);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    public void TryValidateTimeout_AcceptsAllowedValues(int seconds)
    {
        Assert.True(LocalChromeTrafficRules.TryValidateTimeout(seconds, out var normalized, out var error));
        Assert.Equal(seconds, normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(45)]
    [InlineData(120)]
    public void TryValidateTimeout_RejectsOtherValues(int seconds)
    {
        Assert.False(LocalChromeTrafficRules.TryValidateTimeout(seconds, out _, out var error));
        Assert.Contains("30", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryApply_Preset_AndCustomWhenFlagsDiverge()
    {
        Assert.True(LocalChromeTrafficRules.TryApply(
            LocalChromeTrafficRules.ModeEconomic,
            null, null, null, null, null, null,
            LocalChromeTrafficRules.Normal,
            out var economic,
            out var error));
        Assert.Null(error);
        Assert.Equal(LocalChromeTrafficRules.ModeEconomic, economic.Mode);
        Assert.True(economic.BlockMedia);
        Assert.True(economic.BlockAnalytics);
        Assert.True(economic.BlockPrefetch);
        Assert.False(economic.BlockImages);

        Assert.True(LocalChromeTrafficRules.TryApply(
            LocalChromeTrafficRules.ModeAggressive,
            blockMedia: true,
            blockAnalytics: true,
            blockImages: false,
            blockFonts: true,
            blockPrefetch: true,
            navigationTimeoutSeconds: 60,
            LocalChromeTrafficRules.Normal,
            out var custom,
            out error));
        Assert.Null(error);
        Assert.Equal(LocalChromeTrafficRules.ModeCustom, custom.Mode);
        Assert.False(custom.BlockImages);
    }

    [Fact]
    public void FromStored_Timeout90_BecomesCustom()
    {
        var stored = LocalChromeTrafficRules.FromStored(
            LocalChromeTrafficRules.ModeNormal,
            blockMedia: false,
            blockAnalytics: false,
            blockImages: false,
            blockFonts: false,
            blockPrefetch: false,
            navigationTimeoutSeconds: 90);
        Assert.Equal(LocalChromeTrafficRules.ModeCustom, stored.Mode);
        Assert.Equal(90, stored.NavigationTimeoutSeconds);
    }

    [Fact]
    public void Normal_DoesNotBlockAnything()
    {
        AssertNeverBlocked(LocalChromeTrafficRules.Normal);
        Assert.False(Block(LocalChromeTrafficRules.Normal, "Media", "https://avito.ru/clip.mp4"));
        Assert.False(Block(LocalChromeTrafficRules.Normal, "Image", "https://avito.ru/card.jpg"));
        Assert.False(Block(LocalChromeTrafficRules.Normal, "Font", "https://avito.ru/font.woff2"));
        Assert.False(Block(
            LocalChromeTrafficRules.Normal,
            "Other",
            "https://www.avito.ru/prefetch",
            purpose: "prefetch"));
        Assert.False(Block(LocalChromeTrafficRules.Normal, "Script", "https://mc.yandex.ru/metrika.js"));
    }

    [Fact]
    public void Economic_BlocksMediaAnalyticsAndExplicitPrefetch()
    {
        var settings = LocalChromeTrafficRules.Economic;
        Assert.Equal(LocalChromeTrafficBlockKind.Media, Classify(settings, "Media", "https://avito.ru/clip.mp4"));
        Assert.Equal(LocalChromeTrafficBlockKind.Analytics, Classify(settings, "Script", "https://mc.yandex.ru/watch"));
        Assert.Equal(LocalChromeTrafficBlockKind.Analytics, Classify(settings, "Xhr", "https://www.google-analytics.com/g/collect"));
        Assert.Equal(LocalChromeTrafficBlockKind.Analytics, Classify(settings, "Fetch", "https://www.googletagmanager.com/gtm.js"));
        Assert.Equal(LocalChromeTrafficBlockKind.Analytics, Classify(settings, "Image", "https://top.mail.ru/counter"));
        Assert.Equal(LocalChromeTrafficBlockKind.Analytics, Classify(settings, "Other", "https://stats.g.doubleclick.net/pixel"));
        Assert.Equal(
            LocalChromeTrafficBlockKind.Prefetch,
            Classify(settings, "Other", "https://www.avito.ru/next", purpose: "prefetch"));
        Assert.Equal(
            LocalChromeTrafficBlockKind.Prefetch,
            Classify(settings, "Other", "https://www.avito.ru/next", secPurpose: "prefetch;as=document"));
        Assert.False(Block(settings, "Image", "https://www.avito.ru/card.jpg"));
        Assert.False(Block(settings, "Font", "https://www.avito.ru/font.woff2"));
        Assert.False(Block(settings, "Other", "https://www.avito.ru/chunk"));
        AssertNeverBlocked(settings, skipAnalytics: true);
    }

    [Fact]
    public void Aggressive_AdditionallyBlocksImagesAndFonts()
    {
        var settings = LocalChromeTrafficRules.Aggressive;
        Assert.Equal(LocalChromeTrafficBlockKind.Image, Classify(settings, "Image", "https://www.avito.ru/card.jpg"));
        Assert.Equal(LocalChromeTrafficBlockKind.Font, Classify(settings, "Font", "https://www.avito.ru/font.woff2"));
        Assert.Equal(LocalChromeTrafficBlockKind.Media, Classify(settings, "Media", "https://www.avito.ru/clip.mp4"));
        AssertNeverBlocked(settings, skipAnalytics: true);
    }

    [Fact]
    public void ScriptsXhrFetchDocumentStylesheets_AreNeverBlocked_ForNonAnalytics()
    {
        foreach (var settings in new[]
                 {
                     LocalChromeTrafficRules.Normal,
                     LocalChromeTrafficRules.Economic,
                     LocalChromeTrafficRules.Aggressive
                 })
        {
            AssertNeverBlocked(settings, skipAnalytics: true);
        }
    }

    [Fact]
    public void Captcha_TemporarilyAllowsImages()
    {
        var settings = LocalChromeTrafficRules.Aggressive;
        Assert.True(Block(settings, "Image", "https://www.avito.ru/card.jpg"));
        Assert.False(Block(settings, "Image", "https://www.avito.ru/card.jpg", captchaActive: true));
        Assert.False(Block(settings, "Image", "https://gcaptcha4.geetest.com/sprite.png"));
        Assert.True(Block(settings, "Media", "https://www.avito.ru/clip.mp4", captchaActive: true));
    }

    [Fact]
    public void FormatLastRun_UsesCompactRussianSummary()
    {
        Assert.Equal(string.Empty, LocalChromeTrafficRules.FormatLastRun(null));
        Assert.Equal(
            "Последний запуск: 4,2 с · заблокировано: медиа 3, изображения 28",
            LocalChromeTrafficRules.FormatLastRun(4200, 3, 28, 0, 0, 0));
    }

    [Fact]
    public void ManualLogin_DoesNotAttachPolicy()
    {
        var runner = ReadRepoFile("LeadFlow.Core/Services/LocalChrome/LocalChromeLoginSessionRunner.cs");
        Assert.DoesNotContain("LocalChromeTrafficPolicy", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRequestInterception", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("ConditionalWeakTable", runner, StringComparison.Ordinal);
    }

    private static void AssertNeverBlocked(LocalChromeTrafficSettings settings, bool skipAnalytics = false)
    {
        Assert.False(Block(settings, "Document", "https://www.avito.ru/profile"));
        Assert.False(Block(settings, "Stylesheet", "https://www.avito.ru/app.css"));
        Assert.False(Block(settings, "WebSocket", "https://www.avito.ru/socket"));
        if (!skipAnalytics)
        {
            Assert.False(Block(settings, "Script", "https://www.avito.ru/app.js"));
            Assert.False(Block(settings, "Xhr", "https://www.avito.ru/api"));
            Assert.False(Block(settings, "Fetch", "https://www.avito.ru/api"));
        }
        else
        {
            Assert.False(Block(settings, "Script", "https://www.avito.ru/app.js"));
            Assert.False(Block(settings, "Xhr", "https://www.avito.ru/js/1"));
            Assert.False(Block(settings, "Fetch", "https://www.avito.ru/web/1/items"));
        }
    }

    private static bool Block(
        LocalChromeTrafficSettings settings,
        string resourceType,
        string url,
        string? purpose = null,
        string? secPurpose = null,
        bool captchaActive = false) =>
        LocalChromeTrafficRules.ShouldBlock(settings, resourceType, url, purpose, secPurpose, captchaActive);

    private static LocalChromeTrafficBlockKind Classify(
        LocalChromeTrafficSettings settings,
        string resourceType,
        string url,
        string? purpose = null,
        string? secPurpose = null) =>
        LocalChromeTrafficRules.Classify(settings, resourceType, url, purpose, secPurpose, captchaActive: false);

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
