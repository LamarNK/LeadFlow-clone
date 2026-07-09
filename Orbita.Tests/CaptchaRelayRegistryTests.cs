using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CaptchaRelayRegistryTests
{
    [Fact]
    public void ClearConnection_removes_operator_and_worker_ids()
    {
        var registry = new CaptchaRelayRegistry();
        var sessionId = Guid.NewGuid();
        var relay = registry.GetOrAdd(sessionId);
        relay.OperatorConnectionId = "operator-1";
        relay.WorkerConnectionId = "worker-1";
        relay.LastFrame = new CaptchaFrameMessage(
            sessionId,
            "dGVzdA==",
            CaptchaViewportDefaults.Width,
            CaptchaViewportDefaults.Height,
            2);
        relay.LastSnapshot = new CaptchaSnapshotMessage(
            sessionId,
            "dGVzdA==",
            CaptchaViewportDefaults.Width,
            CaptchaViewportDefaults.Height,
            1);

        registry.ClearConnection("operator-1");

        Assert.Null(relay.OperatorConnectionId);
        Assert.Equal("worker-1", relay.WorkerConnectionId);
        Assert.NotNull(relay.LastFrame);
        Assert.NotNull(relay.LastSnapshot);

        registry.ClearConnection("worker-1");

        Assert.Null(relay.WorkerConnectionId);
    }
}
