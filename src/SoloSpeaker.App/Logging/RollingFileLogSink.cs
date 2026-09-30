using System.Text;
using SoloSpeaker.Core.Abstractions;
using SoloSpeaker.Core.Diagnostics;

namespace SoloSpeaker.App.Logging;

/// <summary>
/// ADR 0015's rolling local log: one file per day under the data root, seven days retained,
/// bounded in size, keeping the most recent window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Buffered.</b> <see cref="Write"/> appends to memory and never touches the disk, because
/// the cycle that calls it runs on the same thread as <c>WM_ENDSESSION</c>. The host flushes
/// on the 2 s cadence, so the disk sees one write per cycle rather than one per entry. The
/// cost is that a hard kill loses up to one cadence of lines; that is the same trade §7.3
/// already makes, and the ledger - not the log - is the crash-recovery record.
/// </para>
/// <para>
/// <b>The cap keeps the tail.</b> An earlier draft stopped writing for the rest of the day on
/// reaching the cap. That is fail-silent by construction: a replay flood (§9.2-8, recorded
/// and accepted, needing no attacker) or a Bluetooth reconnect storm reaches the cap in
/// seconds, and the diagnostic would then be dead for up to 24 hours - disabled hardest
/// exactly when something is wrong. Instead the current file rolls to a single <c>.1</c>
/// sibling at half the cap, so total use stays bounded while the last window is always
/// present. Keeping the first half of a flood and discarding the rest is the wrong half.
/// </para>
/// <para>
/// <b>Never throws.</b> A full disk or a revoked permission must not propagate into the
/// reduce cycle. Failures are counted, bounded, and disclosed - see
/// <see cref="DroppedEntries"/>.
/// </para>
/// </remarks>
public sealed class RollingFileLogSink : ILogSink
{
    private const long CapBytes = 10 * 1024 * 1024;
    private const long RollAtBytes = CapBytes / 2;
    private const int RetainedDays = 7;
    private const int FailureLimit = 5;

    private readonly string _directory;
    private readonly IClock _clock;
    private readonly List<LogEntry> _buffered = [];

    private string? _currentPath;
    private DateTimeOffset _currentDay;
    private int _consecutiveFailures;
    private bool _disposed;

    /// <summary>Creates a sink over a resolved log directory.</summary>
    /// <remarks>
    /// The directory is supplied rather than resolved here, because it follows the data root
    /// and the data root is overridable: §7.5 makes it so specifically so two instances can
    /// run on one host, and row 7's single-instance guard is scoped by root. A hardcoded
    /// <c>%LOCALAPPDATA%</c> path would put the first persisted file in the product outside
    /// the only guard it has, and two instances would contend for one handle.
    /// </remarks>
    public RollingFileLogSink(string directory, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(clock);

        _directory = directory;
        _clock = clock;
    }

    /// <summary>How many entries have been lost to write failures since the last disclosure.</summary>
    public int DroppedEntries { get; private set; }

    /// <summary>Whether the sink has stopped attempting writes.</summary>
    public bool HasStopped => _consecutiveFailures >= FailureLimit;

    /// <inheritdoc/>
    public void Write(LogEntry entry)
    {
        if (_disposed || entry is null)
        {
            return;
        }

        _buffered.Add(entry);
    }

    /// <inheritdoc/>
    public void Flush() => Flush(finalDisclosure: false);

    /// <summary>
    /// Writes what is buffered.
    /// </summary>
    /// <param name="finalDisclosure">
    /// When true, one write is attempted even if the sink has stopped, so a graceful exit
    /// always records the gap. An earlier draft disclosed only on the next <em>successful</em>
    /// write, which meant a permanent failure was permanently silent.
    /// </param>
    public void Flush(bool finalDisclosure)
    {
        if (_disposed)
        {
            return;
        }

        if (_buffered.Count == 0 && DroppedEntries == 0)
        {
            return;
        }

        if (HasStopped && !finalDisclosure)
        {
            DroppedEntries += _buffered.Count;
            _buffered.Clear();
            return;
        }

        var text = new StringBuilder();

        if (DroppedEntries > 0)
        {
            text.AppendLine(LogLineFormatter.Format(
                new LogEntry.LogGap(DroppedEntries, "write failures")
                {
                    At = _clock.UtcNow,
                }));
        }

        foreach (LogEntry entry in _buffered)
        {
            text.AppendLine(LogLineFormatter.Format(entry));
        }

        if (TryAppend(text.ToString()))
        {
            _consecutiveFailures = 0;
            DroppedEntries = 0;
        }
        else
        {
            _consecutiveFailures++;
            DroppedEntries += _buffered.Count;
        }

        _buffered.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Flush(finalDisclosure: true);
        _disposed = true;
    }

    private bool TryAppend(string text)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            string path = PathForToday();
            RollIfLarge(path);
            File.AppendAllText(path, text, Encoding.UTF8);

            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string PathForToday()
    {
        DateTimeOffset now = _clock.UtcNow;

        if (_currentPath is not null && now.Date == _currentDay.Date)
        {
            return _currentPath;
        }

        _currentDay = now;
        _currentPath = Path.Combine(_directory, $"solospeaker-{now:yyyyMMdd}.log");

        // A new day is the natural moment to sweep, and it costs one directory listing.
        PurgeOldFiles(now);

        return _currentPath;
    }

    private static void RollIfLarge(string path)
    {
        var file = new FileInfo(path);

        if (!file.Exists || file.Length < RollAtBytes)
        {
            return;
        }

        string previous = path + ".1";

        try
        {
            File.Move(path, previous, overwrite: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A locked sibling is not worth losing the current line over. The next flush
            // retries, and the cap is a bound rather than a guarantee.
        }
    }

    private void PurgeOldFiles(DateTimeOffset now)
    {
        try
        {
            DateTimeOffset cutoff = now.AddDays(-RetainedDays);

            foreach (string path in Directory.EnumerateFiles(_directory, "solospeaker-*.log*"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A locked old log is not worth a failed start.
        }
    }
}
