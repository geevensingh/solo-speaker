using SoloSpeaker.App.Logging;
using SoloSpeaker.Core.Diagnostics;

namespace SoloSpeaker.App.Tests;

public sealed class RollingFileLogSinkTests : IDisposable
{
    private const int RollThresholdBytes = 5 * 1024 * 1024;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"solo-speaker-log-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        else if (File.Exists(_root))
        {
            File.Delete(_root);
        }
    }

    /// <summary>
    /// The cycle calling Write runs on the same thread that handles WM_ENDSESSION. A stalled
    /// disk write there would delay the departure datagram and the endpoint restore, so disk
    /// I/O is permitted only when the host flushes.
    /// </summary>
    [Fact]
    public void Write_alone_touches_no_disk_and_the_file_appears_only_after_flush()
    {
        var clock = new FakeClock();
        using var sink = new RollingFileLogSink(_root, clock);

        sink.Write(Note("buffered", clock));

        Assert.False(Directory.Exists(_root));

        sink.Flush();

        Assert.True(File.Exists(LogPath(clock)));
    }

    [Fact]
    public void The_file_name_carries_the_date_and_crossing_midnight_starts_a_new_file()
    {
        var clock = new FakeClock();
        using var sink = new RollingFileLogSink(_root, clock);

        sink.Write(Note("first", clock));
        sink.Flush();
        clock.Advance(TimeSpan.FromHours(4));
        sink.Write(Note("second", clock));
        sink.Flush();

        Assert.True(File.Exists(Path.Combine(_root, "solospeaker-20260925.log")));
        Assert.True(File.Exists(Path.Combine(_root, "solospeaker-20260926.log")));
    }

    [Fact]
    public void Files_older_than_seven_days_are_deleted_while_files_inside_the_window_survive()
    {
        var clock = new FakeClock();
        string oldPath = Path.Combine(_root, "solospeaker-old.log");
        string recentPath = Path.Combine(_root, "solospeaker-recent.log");
        Directory.CreateDirectory(_root);
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(recentPath, "recent");
        File.SetLastWriteTimeUtc(oldPath, clock.UtcNow.AddDays(-8).UtcDateTime);
        File.SetLastWriteTimeUtc(recentPath, clock.UtcNow.AddDays(-6).UtcDateTime);

        using var sink = new RollingFileLogSink(_root, clock);
        sink.Write(Note("today", clock));
        sink.Flush();

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(recentPath));
    }

    [Fact]
    public void Exceeding_the_roll_threshold_moves_the_current_file_to_point_one_and_starts_fresh()
    {
        var clock = new FakeClock();
        using var sink = new RollingFileLogSink(_root, clock);

        sink.Write(Note(new string('a', RollThresholdBytes), clock));
        sink.Flush();
        sink.Write(Note("tail", clock));
        sink.Flush();

        string currentPath = LogPath(clock);
        string rolledPath = currentPath + ".1";

        Assert.True(File.Exists(rolledPath));
        Assert.True(new FileInfo(rolledPath).Length >= RollThresholdBytes);
        Assert.Contains("tail", File.ReadAllText(currentPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_sink_whose_directory_cannot_be_written_never_throws_and_counts_dropped_entries()
    {
        var clock = new FakeClock();
        File.WriteAllText(_root, "not a directory");
        using var sink = new RollingFileLogSink(_root, clock);

        Exception? failure = Record.Exception(() =>
        {
            sink.Write(Note("lost", clock));
            sink.Flush();
        });

        Assert.Null(failure);
        Assert.Equal(1, sink.DroppedEntries);
    }

    [Fact]
    public void A_later_successful_flush_after_failures_emits_a_log_gap_naming_the_lost_entries()
    {
        var clock = new FakeClock();
        File.WriteAllText(_root, "not a directory");
        using var sink = new RollingFileLogSink(_root, clock);

        sink.Write(Note("lost", clock));
        sink.Flush();
        File.Delete(_root);
        sink.Write(Note("recovered", clock));
        sink.Flush();

        string text = File.ReadAllText(LogPath(clock));
        Assert.Contains("log-gap lost=1", text, StringComparison.Ordinal);
        Assert.Contains("recovered", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a final disclosing flush, a sink that stopped attempting writes after repeated
    /// failures would make a permanent log failure permanently silent.
    /// </summary>
    [Fact]
    public void Dispose_performs_a_final_disclosing_flush_even_after_the_sink_stopped_attempting_writes()
    {
        var clock = new FakeClock();
        File.WriteAllText(_root, "not a directory");
        var sink = new RollingFileLogSink(_root, clock);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            sink.Write(Note($"lost {attempt}", clock));
            sink.Flush();
        }

        Assert.True(sink.HasStopped);
        sink.Write(Note("buffered after stop", clock));
        sink.Flush();
        File.Delete(_root);

        sink.Dispose();

        string text = File.ReadAllText(LogPath(clock));
        Assert.Contains("log-gap lost=6", text, StringComparison.Ordinal);
    }

    private string LogPath(FakeClock clock) =>
        Path.Combine(_root, $"solospeaker-{clock.UtcNow:yyyyMMdd}.log");

    private static LogEntry.Note Note(string text, FakeClock clock) =>
        new(text) { At = clock.UtcNow };
}
