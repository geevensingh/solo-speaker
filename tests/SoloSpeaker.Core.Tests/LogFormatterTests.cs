using SoloSpeaker.Core.Diagnostics;
using SoloSpeaker.Core.PeerLink;
using SoloSpeaker.Core.StateMachine;
using SoloSpeaker.Core.Tests.StateMachine;

namespace SoloSpeaker.Core.Tests;

public sealed class LogFormatterTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 9, 30, 21, 0, 0, TimeSpan.Zero);

    public static TheoryData<LogEntry> Entries =>
        new()
        {
            new LogEntry.OwnershipChanged(ReducerHarness.Id(ReducerHarness.AHex), 7, OwnershipSource.ManualClaim)
            {
                At = Stamp,
            },
            new LogEntry.TrayStateChanged(TrayState.Active, TrayState.Muted, ErrorCause.None)
            {
                At = Stamp,
            },
            new LogEntry.PresenceChanged(Present: false, ViaBye: true)
            {
                At = Stamp,
            },
            new LogEntry.LedgerActivity("write", "endpoint-1", "muted")
            {
                At = Stamp,
            },
            new LogEntry.EndpointChanged("old-endpoint", "new-endpoint")
            {
                At = Stamp,
            },
            new LogEntry.QuarantineChanged("entered", "network up")
            {
                At = Stamp,
            },
            new LogEntry.IngressDrops(IngressResult.BadMac, 12, AnyAccepted: false)
            {
                At = Stamp,
            },
            new LogEntry.LogGap(3, "write failures")
            {
                At = Stamp,
            },
            new LogEntry.Note("started")
            {
                At = Stamp,
            },
        };

    [Theory]
    [MemberData(nameof(Entries))]
    public void Every_log_entry_case_formats_to_exactly_one_line(LogEntry entry)
    {
        string line = LogLineFormatter.Format(entry);

        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void String_fields_are_sanitized_so_attacker_influenced_names_cannot_split_lines()
    {
        var entry = new LogEntry.LedgerActivity("write", "endpoint\r\nid\tone", "detail\tline\r\nnext")
        {
            At = Stamp,
        };

        string line = LogLineFormatter.Format(entry);

        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\t', line);
    }

    /// <summary>
    /// A closed union bounds which fields exist, but a record's generated ToString prints
    /// every member of anything it holds. The pairKey-never-logged guarantee only holds if
    /// no case can be rendered by accident outside LogLineFormatter.
    /// </summary>
    [Theory]
    [MemberData(nameof(Entries))]
    public void To_string_on_each_case_returns_only_the_type_name(LogEntry entry)
    {
        Assert.Equal(entry.GetType().Name, entry.ToString());
    }

    [Fact]
    public void An_ownership_line_contains_the_source_name()
    {
        var entry = new LogEntry.OwnershipChanged(
            ReducerHarness.Id(ReducerHarness.BHex),
            9,
            OwnershipSource.PeerAdoption)
        {
            At = Stamp,
        };

        string line = LogLineFormatter.Format(entry);

        Assert.Contains("source=PeerAdoption", line, StringComparison.Ordinal);
    }
}
