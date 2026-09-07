using System.Reflection;
using LeadFlow.Core.Services.LocalChrome;
using PuppeteerSharp;

namespace Orbita.Tests;

public sealed class LocalChromeProxyAuthTests
{
    [Fact]
    public void IsPageTarget_OnlyPages()
    {
        Assert.True(LocalChromeProxyAuth.IsPageTarget(TargetType.Page));
        Assert.False(LocalChromeProxyAuth.IsPageTarget(TargetType.Worker));
        Assert.False(LocalChromeProxyAuth.IsPageTarget(TargetType.Browser));
    }

    [Fact]
    public async Task ApplyAsync_WithoutCredentials_DoesNotTouchPages()
    {
        var browser = DispatchProxy.Create<IBrowser, FakeBrowserProxy>();
        var fake = (FakeBrowserProxy)(object)browser;
        var options = new LocalChromeLaunchOptions { ProxyEnabled = false };

        await LocalChromeProxyAuth.ApplyAsync(browser, options);

        Assert.Equal(0, fake.PagesCallCount);
        Assert.False(fake.TargetCreatedAttached);
    }

    [Fact]
    public async Task ApplyAsync_WithCredentials_AuthenticatesExistingPages_AndSubscribes()
    {
        var page = DispatchProxy.Create<IPage, FakePageProxy>();
        var pageFake = (FakePageProxy)(object)page;
        var browser = DispatchProxy.Create<IBrowser, FakeBrowserProxy>();
        var fake = (FakeBrowserProxy)(object)browser;
        fake.Pages = [page];
        var options = new LocalChromeLaunchOptions
        {
            ProxyEnabled = true,
            ProxyServer = "203.0.113.10:8080",
            ProxyUsername = "px-user",
            ProxyPassword = "proxy-secret"
        };

        await LocalChromeProxyAuth.ApplyAsync(browser, options);

        Assert.Equal(1, fake.PagesCallCount);
        Assert.True(fake.TargetCreatedAttached);
        Assert.Equal(1, pageFake.AuthenticateCount);
        Assert.Equal("px-user", pageFake.LastUserName);
        Assert.Equal("proxy-secret", pageFake.LastPassword);
    }

    [Fact]
    public async Task HandleNewTarget_AuthenticatesNewPage()
    {
        var page = DispatchProxy.Create<IPage, FakePageProxy>();
        var pageFake = (FakePageProxy)(object)page;
        var browser = DispatchProxy.Create<IBrowser, FakeBrowserProxy>();
        var fake = (FakeBrowserProxy)(object)browser;
        fake.Pages = [];
        var options = new LocalChromeLaunchOptions
        {
            ProxyEnabled = true,
            ProxyUsername = "px",
            ProxyPassword = "secret"
        };
        await LocalChromeProxyAuth.ApplyAsync(browser, options);

        var target = DispatchProxy.Create<ITarget, FakeTargetProxy>();
        var targetFake = (FakeTargetProxy)(object)target;
        targetFake.TypeValue = TargetType.Page;
        targetFake.Page = page;

        await LocalChromeProxyAuth.HandleNewTargetAsync(browser, target);

        Assert.Equal(1, pageFake.AuthenticateCount);
        Assert.Equal("px", pageFake.LastUserName);
    }

    [Fact]
    public async Task HandleNewTarget_IgnoresNonPageTargets()
    {
        var page = DispatchProxy.Create<IPage, FakePageProxy>();
        var pageFake = (FakePageProxy)(object)page;
        var browser = DispatchProxy.Create<IBrowser, FakeBrowserProxy>();
        var fake = (FakeBrowserProxy)(object)browser;
        fake.Pages = [];
        await LocalChromeProxyAuth.ApplyAsync(
            browser,
            new LocalChromeLaunchOptions { ProxyEnabled = true, ProxyUsername = "px", ProxyPassword = "secret" });

        var target = DispatchProxy.Create<ITarget, FakeTargetProxy>();
        var targetFake = (FakeTargetProxy)(object)target;
        targetFake.TypeValue = TargetType.Worker;
        targetFake.Page = page;

        await LocalChromeProxyAuth.HandleNewTargetAsync(browser, target);

        Assert.Equal(0, pageFake.AuthenticateCount);
    }

    public class FakeBrowserProxy : DispatchProxy
    {
        public IPage[] Pages { get; set; } = [];

        public int PagesCallCount { get; private set; }

        public bool TargetCreatedAttached { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == "add_TargetCreated")
            {
                TargetCreatedAttached = true;
                return null;
            }

            if (targetMethod.Name == "PagesAsync")
            {
                PagesCallCount++;
                return Task.FromResult(Pages);
            }

            if (targetMethod.ReturnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (targetMethod.ReturnType.IsValueType)
            {
                return Activator.CreateInstance(targetMethod.ReturnType);
            }

            return null;
        }
    }

    public class FakePageProxy : DispatchProxy
    {
        public int AuthenticateCount { get; private set; }

        public string? LastUserName { get; private set; }

        public string? LastPassword { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == "AuthenticateAsync")
            {
                AuthenticateCount++;
                if (args is { Length: > 0 } && args[0] is Credentials credentials)
                {
                    LastUserName = credentials.Username;
                    LastPassword = credentials.Password;
                }

                return Task.CompletedTask;
            }

            if (targetMethod.ReturnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            return null;
        }
    }

    public class FakeTargetProxy : DispatchProxy
    {
        public TargetType TypeValue { get; set; } = TargetType.Page;

        public IPage? Page { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == "get_Type")
            {
                return TypeValue;
            }

            if (targetMethod.Name == "PageAsync")
            {
                return Task.FromResult(Page);
            }

            return null;
        }
    }
}
