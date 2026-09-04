using System.Reflection;
using LeadFlow.Core.Services.Avito;
using PuppeteerSharp;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoHumanPointerCancellationTests
{
    [Fact]
    public async Task TryClickHandleAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var page = DispatchProxy.Create<IPage, NoopPageProxy>();
        var handle = DispatchProxy.Create<IElementHandle, NoopHandleProxy>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Отмена должна всплыть как OperationCanceledException, а не быть проглоченной
        // в `catch { return false; }` — иначе воркфлоу продолжил бы к следующему шагу.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => AvitoHumanPointer.TryClickHandleAsync(page, handle, cts.Token));
    }

    [Fact]
    public async Task TryClickHandleAsync_CancellationDuringScroll_RethrowsInsteadOfReturningFalse()
    {
        var page = DispatchProxy.Create<IPage, NoopPageProxy>();
        var handle = DispatchProxy.Create<IElementHandle, CancellingHandleProxy>();
        using var cts = new CancellationTokenSource();

        // Отмена происходит во время первого шага (scrollIntoView) — до клика.
        ((CancellingHandleProxy)GetProxy(handle)).CancellationTokenSource = cts;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => AvitoHumanPointer.TryClickHandleAsync(page, handle, cts.Token));
    }

    private static object GetProxy(object proxy) => proxy;

    // Прокси, которые никогда не должны вызываться в этих тестах: первый же
    // ThrowIfCancellationRequested() прерывает выполнение до обращения к page/handle.
    public class NoopPageProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"IPage.{targetMethod?.Name} не должен вызываться в этом тесте.");
    }

    public class NoopHandleProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"IElementHandle.{targetMethod?.Name} не должен вызываться в этом тесте.");
    }

    public class CancellingHandleProxy : DispatchProxy
    {
        public CancellationTokenSource? CancellationTokenSource { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IElementHandle.EvaluateFunctionAsync))
            {
                CancellationTokenSource!.Cancel();
                throw new OperationCanceledException(CancellationTokenSource.Token);
            }

            throw new InvalidOperationException($"IElementHandle.{targetMethod?.Name} не должен вызываться в этом тесте.");
        }
    }
}
