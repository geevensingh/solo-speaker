using SoloSpeaker.Core.Abstractions;

namespace SoloSpeaker.Core.Tests.TwoNode;

/// <summary>
/// A simulated local subnet: every send reaches every subscriber, <b>including the sender</b>.
/// </summary>
/// <remarks>
/// <para>
/// Modelling a broadcast medium rather than a point-to-point pipe is not decoration. An IPv4
/// subnet broadcast is delivered to every local socket bound to the port, the sender's
/// included - which is exactly why design §7.1 drops self-origin datagrams at ingress step 3,
/// and why revision 6 records that failing to do so leaves a machine "muted for a peer that
/// no longer exists". A pipe would make <c>IngressResult.SelfOrigin</c> unreachable inside
/// the loop, so the harness built to find that class of defect could not see it.
/// </para>
/// <para>
/// <b>Delivery is queued, never inline.</b> <see cref="IPeerTransport.DatagramReceived"/> is
/// a push event, so delivering synchronously inside <see cref="Send"/> would re-enter the
/// reducer while the outer reduction is still on the stack and clobber the state it holds.
/// Everything is enqueued and released by <see cref="Deliver"/>.
/// </para>
/// </remarks>
internal sealed class SubnetSimulator
{
    private readonly List<Endpoint> _endpoints = [];
    private readonly List<Pending> _pending = [];
    private readonly FakeClockReader _now;

    internal SubnetSimulator(FakeClockReader now) => _now = now;

    /// <summary>Drop every datagram sent while this is true - the loss scenario.</summary>
    internal bool DropEverything { get; set; }

    /// <summary>Deliver each datagram twice.</summary>
    internal bool DuplicateEverything { get; set; }

    /// <summary>Hold sends in flight until <see cref="Release"/>, for the simultaneous-claim case.</summary>
    internal bool HoldInFlight { get; set; }

    /// <summary>Delivered datagrams, newest last, for replay scenarios.</summary>
    internal List<byte[]> Delivered { get; } = [];

    /// <summary>Every datagram sent, tagged with its sender, newest last.</summary>
    internal List<(string Sender, byte[] Payload)> Sent { get; } = [];

    /// <summary>The most recent datagram a given node put on the wire.</summary>
    internal byte[] LastSentBy(string name) =>
        Sent.LastOrDefault(sent => sent.Sender == name).Payload
        ?? throw new InvalidOperationException($"Node '{name}' has not broadcast.");

    internal IPeerTransport ConnectEndpoint(string name)
    {
        var endpoint = new Endpoint(name, this);
        _endpoints.Add(endpoint);
        return endpoint;
    }

    /// <summary>
    /// Takes a node off the subnet entirely: it neither sends nor receives. That is what
    /// "asleep" means for §4.3's lid-open scenario - a machine that was genuinely off, rather
    /// than one that is merely not listening.
    /// </summary>
    internal void Disconnect(string name) => Find(name).Connected = false;

    internal void Reconnect(string name) => Find(name).Connected = true;

    /// <summary>Releases everything held by <see cref="HoldInFlight"/>.</summary>
    internal void Release() => HoldInFlight = false;

    /// <summary>
    /// Delivers everything due at or before the current clock reading. Returns how many
    /// datagrams were handed to a subscriber.
    /// </summary>
    internal int Deliver()
    {
        if (HoldInFlight)
        {
            return 0;
        }

        int delivered = 0;
        Pending[] due = [.. _pending.Where(pending => pending.DueAt <= _now())];

        foreach (Pending pending in due)
        {
            _pending.Remove(pending);

            if (!pending.Target.Connected)
            {
                continue;
            }

            Delivered.Add(pending.Payload);
            pending.Target.Receive(pending.Payload);
            delivered++;
        }

        return delivered;
    }

    /// <summary>Injects raw bytes at one endpoint, for replay and hostile-input scenarios.</summary>
    internal void Inject(string targetName, byte[] payload) =>
        _pending.Add(new Pending(Find(targetName), payload, _now()));

    private Endpoint Find(string name) =>
        _endpoints.Single(endpoint => endpoint.Name == name);

    private void Broadcast(Endpoint sender, ReadOnlyMemory<byte> datagram)
    {
        // A disconnected node is off the subnet in both directions. Suppressing only
        // delivery would leave a "sleeping" machine still heard by its peer.
        if (DropEverything || !sender.Connected)
        {
            return;
        }

        byte[] payload = datagram.ToArray();
        int copies = DuplicateEverything ? 2 : 1;

        Sent.Add((sender.Name, payload));

        // Every subscriber, sender included. That is what a broadcast does.
        foreach (Endpoint endpoint in _endpoints)
        {
            for (int copy = 0; copy < copies; copy++)
            {
                _pending.Add(new Pending(endpoint, payload, _now() + endpoint.InboundDelay));
            }
        }

        sender.SendCount++;
    }

    private sealed record Pending(Endpoint Target, byte[] Payload, TimeSpan DueAt);

    private sealed class Endpoint : IPeerTransport
    {
        private readonly SubnetSimulator _subnet;

        internal Endpoint(string name, SubnetSimulator subnet)
        {
            Name = name;
            _subnet = subnet;
        }

        public event Action<ReadOnlyMemory<byte>>? DatagramReceived;

        public event Action? NetworkChanged;

        internal string Name { get; }

        internal bool Connected { get; set; } = true;

        internal int SendCount { get; set; }

        /// <summary>Per-recipient latency, which is how reordering is produced.</summary>
        internal TimeSpan InboundDelay { get; set; }

        public bool IsBound => true;

        public void Send(ReadOnlyMemory<byte> datagram) => _subnet.Broadcast(this, datagram);

        internal void Receive(byte[] payload) => DatagramReceived?.Invoke(payload);

        internal void RaiseNetworkChanged() => NetworkChanged?.Invoke();
    }
}

/// <summary>Reads the current monotonic time. Keeps the simulator off <c>IClock</c> directly.</summary>
internal delegate TimeSpan FakeClockReader();
