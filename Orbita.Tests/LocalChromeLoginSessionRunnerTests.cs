using LeadFlow.Core.Models;
using LeadFlow.Core.Services.LocalChrome;
using PuppeteerSharp;
using System.Reflection;

namespace Orbita.Tests;

public sealed class LocalChromeLoginSessionRunnerTests
{
    [Fact]
    public async Task RunAsync_LaunchesChrome_WithoutMonitoringSession()
    {
        var accountLock = new LocalChromeAccountLock();
        var launcher = new FakeLocalChromeLauncher();
        var sut = new LocalChromeLoginSessionRunner(launcher, accountLock);
        var account = new AvitoAccount
        {
            Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            DisplayName = "chrome-acc",
            ProfileProvider = AvitoProfileProvider.Local,
            BrowserProfilePath = @"D:\Orbita\ChromeProfiles\acc-1"
        };

        await sut.RunAsync(account, CancellationToken.None);

        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(account.Id, launcher.LastAccountId);
        Assert.Equal(0, launcher.LastBrowser!.CloseCount);
        Assert.Equal(1, launcher.LastBrowser.DisposeCount);
        Assert.False(accountLock.IsHeld(account.Id));
        Assert.Equal(AvitoAccountStatus.NotConfigured, account.Status);
    }

    [Fact]
    public async Task RunAsync_WhenMonitoringHeld_DoesNotLaunch()
    {
        var accountLock = new LocalChromeAccountLock();
        var launcher = new FakeLocalChromeLauncher();
        var sut = new LocalChromeLoginSessionRunner(launcher, accountLock);
        var account = new AvitoAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "chrome-acc",
            ProfileProvider = AvitoProfileProvider.Local,
            BrowserProfilePath = @"D:\Orbita\ChromeProfiles\acc-1"
        };
        Assert.True(accountLock.TryAcquire(account.Id, LocalChromeAccountLock.Monitoring, out _));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunAsync(account, CancellationToken.None));

        Assert.Contains("мониторинга", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, launcher.LaunchCount);
        Assert.DoesNotContain("D:\\Orbita", ex.Message, StringComparison.Ordinal);
    }

    private sealed class FakeLocalChromeLauncher : ILocalChromeBrowserLauncher
    {
        public int LaunchCount { get; private set; }

        public Guid LastAccountId { get; private set; }

        public FakeBrowserProxy? LastBrowser { get; private set; }

        public Task<IBrowser> LaunchAsync(
            LocalChromeLaunchOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaunchCount++;
            LastAccountId = options.AccountId;
            var browser = DispatchProxy.Create<IBrowser, FakeBrowserProxy>();
            LastBrowser = (FakeBrowserProxy)(object)browser;
            LastBrowser.Connected = false;
            return Task.FromResult(browser);
        }
    }

    public class FakeBrowserProxy : DispatchProxy
    {
        public int CloseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool Connected { get; set; } = true;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == "get_IsConnected")
            {
                return Connected;
            }

            if (targetMethod.Name == "CloseAsync")
            {
                CloseCount++;
                Connected = false;
                return Task.CompletedTask;
            }

            if (targetMethod.Name is "Dispose" or "DisposeAsync")
            {
                DisposeCount++;
                if (targetMethod.ReturnType == typeof(ValueTask))
                {
                    return ValueTask.CompletedTask;
                }

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
