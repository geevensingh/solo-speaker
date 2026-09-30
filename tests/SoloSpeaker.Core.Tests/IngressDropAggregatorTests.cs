using SoloSpeaker.Core.Diagnostics;
using SoloSpeaker.Core.PeerLink;

namespace SoloSpeaker.Core.Tests;

public sealed class IngressDropAggregatorTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 9, 30, 21, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// ADR 0015's load-bearing rule: a hostile or mismatched peer can send hundreds of
    /// rejected datagrams per minute, and a per-datagram log would bury the signal in the
    /// noise it is supposed to explain.
    /// </summary>
    [Fact]
    public void Five_hundred_bad_mac_drops_inside_one_minute_emit_one_aggregate_line()
    {
        var aggregator = new IngressDropAggregator();

        for (int index = 0; index < 500; index++)
        {
            aggregator.Observe(IngressResult.BadMac, TimeSpan.FromMilliseconds(index));
        }

        LogEntry entry = Assert.Single(aggregator.Flush(TimeSpan.FromMinutes(1), Stamp));
        LogEntry.IngressDrops drops = Assert.IsType<LogEntry.IngressDrops>(entry);

        Assert.Equal(IngressResult.BadMac, drops.Reason);
        Assert.Equal(500, drops.Count);
    }

    [Fact]
    public void A_quiet_minute_emits_no_zero_count_entries()
    {
        var aggregator = new IngressDropAggregator();

        aggregator.Observe(IngressResult.Accepted, TimeSpan.Zero);

        Assert.Empty(aggregator.Flush(TimeSpan.FromMinutes(1), Stamp));
    }

    /// <summary>
    /// Design section 7.1 drops a machine's own broadcast at ingress step 3. Counting that
    /// expected echo would turn a healthy machine into a 43,200-lines-per-day diagnostic.
    /// </summary>
    [Fact]
    public void Self_origin_is_never_counted_as_a_drop()
    {
        var aggregator = new IngressDropAggregator();

        for (int index = 0; index < 20; index++)
        {
            aggregator.Observe(IngressResult.SelfOrigin, TimeSpan.FromSeconds(index));
        }

        Assert.Empty(aggregator.Flush(TimeSpan.FromMinutes(1), Stamp));
    }

    [Fact]
    public void Accepted_datagrams_are_not_counted_but_set_any_accepted_on_emitted_entries()
    {
        var aggregator = new IngressDropAggregator();

        aggregator.Observe(IngressResult.BadMac, TimeSpan.Zero);
        aggregator.Observe(IngressResult.Accepted, TimeSpan.FromSeconds(1));

        LogEntry.IngressDrops drops = Assert.IsType<LogEntry.IngressDrops>(
            Assert.Single(aggregator.Flush(TimeSpan.FromMinutes(1), Stamp)));

        Assert.Equal(1, drops.Count);
        Assert.True(drops.AnyAccepted);
    }

    /// <summary>
    /// This is the G9 discriminator: a bare BadMac count cannot distinguish an unverifiable
    /// peer from ordinary foreign noise unless the same window says nothing valid was
    /// accepted.
    /// </summary>
    [Fact]
    public void Drops_without_any_accepted_datagram_emit_any_accepted_false()
    {
        var aggregator = new IngressDropAggregator();

        aggregator.Observe(IngressResult.BadMac, TimeSpan.Zero);

        LogEntry.IngressDrops drops = Assert.IsType<LogEntry.IngressDrops>(
            Assert.Single(aggregator.Flush(TimeSpan.FromMinutes(1), Stamp)));

        Assert.False(drops.AnyAccepted);
    }

    [Fact]
    public void Flushing_before_a_minute_elapsed_emits_nothing_unless_forced()
    {
        var aggregator = new IngressDropAggregator();

        aggregator.Observe(IngressResult.BadMac, TimeSpan.Zero);

        Assert.Empty(aggregator.Flush(TimeSpan.FromSeconds(59), Stamp));

        LogEntry entry = Assert.Single(aggregator.Flush(TimeSpan.FromSeconds(59), Stamp, force: true));
        Assert.IsType<LogEntry.IngressDrops>(entry);
    }

    [Fact]
    public void Counts_reset_after_a_flush()
    {
        var aggregator = new IngressDropAggregator();

        aggregator.Observe(IngressResult.BadMac, TimeSpan.Zero);
        Assert.Single(aggregator.Flush(TimeSpan.FromMinutes(1), Stamp));

        aggregator.Observe(IngressResult.BadMac, TimeSpan.FromMinutes(1));
        LogEntry.IngressDrops drops = Assert.IsType<LogEntry.IngressDrops>(
            Assert.Single(aggregator.Flush(TimeSpan.FromMinutes(2), Stamp)));

        Assert.Equal(1, drops.Count);
    }
}
