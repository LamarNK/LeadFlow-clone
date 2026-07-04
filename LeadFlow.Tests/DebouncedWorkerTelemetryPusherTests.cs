using LeadFlow.Core.Services.Worker;
using Xunit;

namespace LeadFlow.Tests;

public sealed class DebouncedWorkerTelemetryPusherTests
{
    [Fact]
    public async Task RequestDebouncedPush_CoalescesRapidCalls()
    {
        var sink = new CountingTelemetrySink();
        var pusher = new DebouncedWorkerTelemetryPusher(sink, TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource();

        pusher.RequestDebouncedPush(cts.Token);
        pusher.RequestDebouncedPush(cts.Token);
        pusher.RequestDebouncedPush(cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(120));

        Assert.Equal(1, sink.PushCount);
    }

    [Fact]
    public async Task PushNowAsync_CancelsPendingDebouncedPush()
    {
        var sink = new CountingTelemetrySink();
        var pusher = new DebouncedWorkerTelemetryPusher(sink, TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource();

        pusher.RequestDebouncedPush(cts.Token);
        await pusher.PushNowAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(120));

        Assert.Equal(1, sink.PushCount);
    }

    [Fact]
    public async Task PushNowAsync_PushesImmediately()
    {
        var sink = new CountingTelemetrySink();
        var pusher = new DebouncedWorkerTelemetryPusher(sink);

        await pusher.PushNowAsync(CancellationToken.None);

        Assert.Equal(1, sink.PushCount);
    }

    private sealed class CountingTelemetrySink : IWorkerTelemetrySink
    {
        public int PushCount { get; private set; }

        public Task PushSnapshotAsync(CancellationToken cancellationToken = default)
        {
            PushCount++;
            return Task.CompletedTask;
        }
    }
}