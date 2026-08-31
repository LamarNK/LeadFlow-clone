using LeadFlow.Core.Services.LocalChrome;

namespace Orbita.Tests;

public sealed class LocalChromeAccountLockTests
{
    [Fact]
    public void SameAccount_CannotHoldLoginAndMonitoringTogether()
    {
        var lockObj = new LocalChromeAccountLock();
        var accountId = Guid.NewGuid();

        Assert.True(lockObj.TryAcquire(accountId, LocalChromeAccountLock.Login, out _));
        Assert.False(lockObj.TryAcquire(accountId, LocalChromeAccountLock.Monitoring, out var existing));
        Assert.Equal(LocalChromeAccountLock.Login, existing);
        Assert.True(lockObj.IsHeld(accountId, LocalChromeAccountLock.Login));
        Assert.False(lockObj.IsHeld(accountId, LocalChromeAccountLock.Monitoring));

        lockObj.Release(accountId, LocalChromeAccountLock.Login);
        Assert.True(lockObj.TryAcquire(accountId, LocalChromeAccountLock.Monitoring, out _));
        Assert.False(lockObj.TryAcquire(accountId, LocalChromeAccountLock.Login, out existing));
        Assert.Equal(LocalChromeAccountLock.Monitoring, existing);
    }

    [Fact]
    public void DifferentAccounts_CanRunInParallel()
    {
        var lockObj = new LocalChromeAccountLock();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.True(lockObj.TryAcquire(first, LocalChromeAccountLock.Monitoring, out _));
        Assert.True(lockObj.TryAcquire(second, LocalChromeAccountLock.Login, out _));
    }
}
