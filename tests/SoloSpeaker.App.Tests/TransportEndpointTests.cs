using System.Net;
using SoloSpeaker.App.PeerLink;

namespace SoloSpeaker.App.Tests;

public sealed class TransportEndpointTests
{
    [Fact]
    public void Exclusive_and_shared_endpoints_differ_only_in_port_sharing()
    {
        TransportEndpoint exclusive = TransportEndpoint.Exclusive(48911);
        TransportEndpoint shared = TransportEndpoint.Shared(48911);

        Assert.False(exclusive.AllowPortSharing);
        Assert.True(shared.AllowPortSharing);
        Assert.Equal(exclusive.Port, shared.Port);
        Assert.Equal(exclusive.BindTarget, shared.BindTarget);
        Assert.Equal(exclusive.BroadcastTarget, shared.BroadcastTarget);
    }

    /// <summary>
    /// Decision D-A uses one UDP port for both directions; two instances on different ports
    /// would each broadcast where the other never listens.
    /// </summary>
    [Fact]
    public void Broadcast_and_bind_use_the_configured_port_with_broadcast_and_any_addresses()
    {
        TransportEndpoint endpoint = TransportEndpoint.Shared(48912);

        Assert.Equal(IPAddress.Broadcast, endpoint.BroadcastTarget.Address);
        Assert.Equal(IPAddress.Any, endpoint.BindTarget.Address);
        Assert.Equal(48912, endpoint.BroadcastTarget.Port);
        Assert.Equal(48912, endpoint.BindTarget.Port);
    }
}
