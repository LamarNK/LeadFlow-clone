using System.Reflection;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.LocalChrome;
using Orbita.Contracts;
using PuppeteerSharp;

namespace Orbita.Tests;

public sealed class LocalChromeTrafficPolicyTests
{
    [Fact]
    public async Task Policy_RemainsAvailableForPage_AfterAsyncFactoryScopeReturns()
    {
        var page = FakePage.Create();
        await FactoryScopeAsync(page, Account(timeoutSeconds: 30));

        var bound = LocalChromeTrafficPolicy.ForPage(page);
        Assert.NotNull(bound);
        Assert.Equal(30_000, LocalChromeTrafficPolicy.ResolveNavigationTimeoutMs(page, 60_000));
        Assert.Equal(30_000, page.DefaultNavigationTimeout);
        Assert.True(bound!.IsMonitoringSession);

        using (LocalChromeTrafficPolicy.AllowImages(page))
        {
            Assert.True(bound.CaptchaImagesAllowed);
            Assert.Equal(
                LocalChromeTrafficBlockKind.None,
                bound.Classify("Image", "https://www.avito.ru/card.jpg"));
        }
    }

    [Fact]
    public async Task AllowImages_AllowsImageInPolicyCallback()
    {
        var page = FakePage.Create();
        var policy = LocalChromeTrafficPolicy.BeginMonitoring(Account(blockImages: true, blockMedia: true));
        await policy.AttachAsync(page);

        Assert.Equal(
            LocalChromeTrafficBlockKind.Image,
            policy.Classify("Image", "https://www.avito.ru/card.jpg"));

        using (LocalChromeTrafficPolicy.AllowImages(page))
        {
            Assert.True(policy.CaptchaImagesAllowed);
            Assert.Equal(
                LocalChromeTrafficBlockKind.None,
                policy.Classify("Image", "https://www.avito.ru/card.jpg"));
            Assert.Equal(
                LocalChromeTrafficBlockKind.Media,
                policy.Classify("Media", "https://www.avito.ru/clip.mp4"));
        }

        Assert.False(policy.CaptchaImagesAllowed);
        Assert.Equal(
            LocalChromeTrafficBlockKind.Image,
            policy.Classify("Image", "https://www.avito.ru/card.jpg"));
    }

    [Fact]
    public async Task Dispose_RemovesPageMapping()
    {
        var page = FakePage.Create();
        var policy = LocalChromeTrafficPolicy.BeginMonitoring(Account(timeoutSeconds: 90));
        await policy.AttachAsync(page);
        Assert.Same(policy, LocalChromeTrafficPolicy.ForPage(page));

        await policy.DisposeAsync();

        Assert.Null(LocalChromeTrafficPolicy.ForPage(page));
        Assert.Equal(45_000, LocalChromeTrafficPolicy.ResolveNavigationTimeoutMs(page, 45_000));
        using (LocalChromeTrafficPolicy.AllowImages(page))
        {
            Assert.False(policy.CaptchaImagesAllowed);
        }
    }

    [Fact]
    public async Task LocalPage_UsesConfiguredTimeout_UnmappedPagesKeepFallback()
    {
        var local30 = FakePage.Create();
        var local90 = FakePage.Create();
        var adsPower = FakePage.Create();
        var multilogin = FakePage.Create();

        var policy30 = LocalChromeTrafficPolicy.BeginMonitoring(Account(timeoutSeconds: 30));
        var policy90 = LocalChromeTrafficPolicy.BeginMonitoring(Account(timeoutSeconds: 90));
        await policy30.AttachAsync(local30);
        await policy90.AttachAsync(local90);

        Assert.Equal(30_000, LocalChromeTrafficPolicy.ResolveNavigationTimeoutMs(local30, 60_000));
        Assert.Equal(90_000, LocalChromeTrafficPolicy.ResolveNavigationTimeoutMs(local90, 15_000));
        Assert.Equal(30_000, local30.DefaultNavigationTimeout);
        Assert.Equal(90_000, local90.DefaultNavigationTimeout);
        Assert.Equal(60_000, LocalChromeTrafficPolicy.ResolveNavigationTimeoutMs(adsPower, 60_000));
        Assert.Equal(45_000, LocalChromeTrafficPolicy.ResolveNavigationTimeoutMs(multilogin, 45_000));
        Assert.Equal(90_000, LocalChromeTrafficPolicy.ResolveNavigationTimeoutMs(page: null, 90_000));
        using (LocalChromeTrafficPolicy.AllowImages(adsPower))
        {
            Assert.Null(LocalChromeTrafficPolicy.ForPage(adsPower));
        }
    }

    [Fact]
    public async Task DifferentPages_DoNotMixStatsAndCaptchaDepth()
    {
        var pageA = FakePage.Create();
        var pageB = FakePage.Create();
        var policyA = LocalChromeTrafficPolicy.BeginMonitoring(Account(blockImages: true, blockMedia: true));
        var policyB = LocalChromeTrafficPolicy.BeginMonitoring(Account(blockImages: true, blockFonts: true));
        await policyA.AttachAsync(pageA);
        await policyB.AttachAsync(pageB);

        using (LocalChromeTrafficPolicy.AllowImages(pageA))
        {
            Assert.True(policyA.CaptchaImagesAllowed);
            Assert.False(policyB.CaptchaImagesAllowed);
            Assert.Equal(
                LocalChromeTrafficBlockKind.None,
                policyA.Classify("Image", "https://www.avito.ru/a.jpg"));
            Assert.Equal(
                LocalChromeTrafficBlockKind.Image,
                policyB.Classify("Image", "https://www.avito.ru/b.jpg"));
        }

        Assert.Equal(
            LocalChromeTrafficBlockKind.Media,
            policyA.Classify("Media", "https://www.avito.ru/a.mp4"));
        Assert.Equal(
            LocalChromeTrafficBlockKind.Font,
            policyB.Classify("Font", "https://www.avito.ru/b.woff2"));
        Assert.Equal(
            LocalChromeTrafficBlockKind.None,
            policyB.Classify("Media", "https://www.avito.ru/b.mp4"));

        var statsA = policyA.Snapshot();
        var statsB = policyB.Snapshot();
        Assert.Equal(1, statsA.BlockedMedia);
        Assert.Equal(0, statsA.BlockedImages);
        Assert.Equal(0, statsA.BlockedFonts);
        Assert.Equal(1, statsB.BlockedImages);
        Assert.Equal(1, statsB.BlockedFonts);
        Assert.Equal(0, statsB.BlockedMedia);
        Assert.NotSame(policyA, LocalChromeTrafficPolicy.ForPage(pageB));
        Assert.Same(policyA, LocalChromeTrafficPolicy.ForPage(pageA));
        Assert.Same(policyB, LocalChromeTrafficPolicy.ForPage(pageB));

        await policyA.DisposeAsync();
        Assert.Null(LocalChromeTrafficPolicy.ForPage(pageA));
        Assert.Same(policyB, LocalChromeTrafficPolicy.ForPage(pageB));
        Assert.False(policyB.CaptchaImagesAllowed);
    }

    [Fact]
    public async Task Attach_MovesMappingToNewPage()
    {
        var first = FakePage.Create();
        var second = FakePage.Create();
        var policy = LocalChromeTrafficPolicy.BeginMonitoring(Account(timeoutSeconds: 60, blockImages: true));
        await policy.AttachAsync(first);
        Assert.Same(policy, LocalChromeTrafficPolicy.ForPage(first));

        await policy.AttachAsync(second);

        Assert.Null(LocalChromeTrafficPolicy.ForPage(first));
        Assert.Same(policy, LocalChromeTrafficPolicy.ForPage(second));
        Assert.Equal(60_000, LocalChromeTrafficPolicy.ResolveNavigationTimeoutMs(second, 15_000));
        Assert.Equal(15_000, LocalChromeTrafficPolicy.ResolveNavigationTimeoutMs(first, 15_000));
        using (LocalChromeTrafficPolicy.AllowImages(second))
        {
            Assert.True(policy.CaptchaImagesAllowed);
            Assert.Equal(
                LocalChromeTrafficBlockKind.None,
                policy.Classify("Image", "https://www.avito.ru/card.jpg"));
        }

        using (LocalChromeTrafficPolicy.AllowImages(first))
        {
            Assert.False(policy.CaptchaImagesAllowed);
        }
    }

    private static async Task FactoryScopeAsync(IPage page, AvitoAccount account)
    {
        var policy = LocalChromeTrafficPolicy.BeginMonitoring(account);
        await Task.Yield();
        await policy.AttachAsync(page);
        await Task.Yield();
    }

    private static AvitoAccount Account(
        int timeoutSeconds = 60,
        bool blockImages = false,
        bool blockMedia = false,
        bool blockFonts = false) =>
        new()
        {
            LocalTrafficMode = LocalChromeTrafficRules.ModeCustom,
            LocalBlockMedia = blockMedia,
            LocalBlockAnalytics = false,
            LocalBlockImages = blockImages,
            LocalBlockFonts = blockFonts,
            LocalBlockPrefetch = false,
            LocalNavigationTimeoutSeconds = timeoutSeconds
        };

    public static class FakePage
    {
        public static IPage Create() => DispatchProxy.Create<IPage, FakePageProxy>();
    }

    public class FakePageProxy : DispatchProxy
    {
        public int DefaultNavigationTimeout { get; set; }

        public bool InterceptionEnabled { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == "set_DefaultNavigationTimeout")
            {
                DefaultNavigationTimeout = args is { Length: > 0 } && args[0] is int timeout
                    ? timeout
                    : DefaultNavigationTimeout;
                return null;
            }

            if (targetMethod.Name == "get_DefaultNavigationTimeout")
            {
                return DefaultNavigationTimeout;
            }

            if (targetMethod.Name == "SetRequestInterceptionAsync")
            {
                InterceptionEnabled = args is { Length: > 0 } && args[0] is true;
                return Task.CompletedTask;
            }

            if (targetMethod.Name is "add_Request" or "remove_Request" or "add_Load" or "remove_Load")
            {
                return null;
            }

            if (targetMethod.ReturnType == typeof(Task) || targetMethod.ReturnType.FullName == "System.Threading.Tasks.Task")
            {
                return Task.CompletedTask;
            }

            if (targetMethod.ReturnType.IsGenericType
                && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = targetMethod.ReturnType.GetGenericArguments()[0];
                var result = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, [result]);
            }

            if (targetMethod.ReturnType == typeof(ValueTask))
            {
                return ValueTask.CompletedTask;
            }

            if (targetMethod.ReturnType.IsValueType)
            {
                return Activator.CreateInstance(targetMethod.ReturnType);
            }

            return null;
        }
    }
}
