using LeadFlow.Core.Services.LocalChrome;
using PuppeteerSharp;
using Xunit;

namespace LeadFlow.Tests;

public sealed class LocalChromeBrowserLauncherRetryTests
{
    [Fact]
    public void ShouldRetryLaunch_RetriesProcessExceptionBeforeLastAttempt()
    {
        var exception = new ProcessException("Failed to launch browser!");

        Assert.True(LocalChromeBrowserLauncher.ShouldRetryLaunch(0, exception));
        Assert.True(LocalChromeBrowserLauncher.ShouldRetryLaunch(1, exception));
        Assert.False(LocalChromeBrowserLauncher.ShouldRetryLaunch(2, exception));
    }

    [Fact]
    public void ShouldRetryLaunch_RetriesNestedProcessException()
    {
        var exception = new InvalidOperationException(
            "launch wrapper",
            new ProcessException("Failed to launch browser!"));

        Assert.True(LocalChromeBrowserLauncher.ShouldRetryLaunch(0, exception));
    }

    [Fact]
    public void ShouldRetryLaunch_DoesNotRetryUnrelatedFailure()
    {
        var exception = new InvalidOperationException("Не удалось авторизовать прокси.");

        Assert.False(LocalChromeBrowserLauncher.ShouldRetryLaunch(0, exception));
    }
}
