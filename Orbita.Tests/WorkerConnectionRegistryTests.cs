using Orbita.Api.Services;

namespace Orbita.Tests;

public class WorkerConnectionRegistryTests
{
    [Fact]
    public void Register_tracks_connection_and_detects_online()
    {
        var registry = new WorkerConnectionRegistry();
        var workerId = Guid.NewGuid();

        var registration = registry.Register(workerId, "conn-1");

        Assert.True(registration.Success);
        Assert.Null(registration.DisplacedConnectionId);
        Assert.True(registry.IsConnected(workerId));
        Assert.True(registry.TryGetConnectionId(workerId, out var connectionId));
        Assert.Equal("conn-1", connectionId);
    }

    [Fact]
    public void Register_replaces_previous_connection_for_same_worker()
    {
        var registry = new WorkerConnectionRegistry();
        var workerId = Guid.NewGuid();

        registry.Register(workerId, "conn-1");
        var registration = registry.Register(workerId, "conn-2");

        Assert.Equal("conn-1", registration.DisplacedConnectionId);
        Assert.True(registry.TryGetConnectionId(workerId, out var connectionId));
        Assert.Equal("conn-2", connectionId);
    }

    [Fact]
    public void Unregister_removes_worker_from_online_set()
    {
        var registry = new WorkerConnectionRegistry();
        var workerId = Guid.NewGuid();
        registry.Register(workerId, "conn-1");

        Assert.True(registry.TryUnregister("conn-1", out var info));
        Assert.NotNull(info);
        Assert.Equal(workerId, info!.WorkerId);
        Assert.False(registry.IsConnected(workerId));
    }
}