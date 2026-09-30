namespace SoloSpeaker.App.Hosting;

/// <summary>
/// ADR 0012's single-instance guard: one running SoloSpeaker per data root.
/// </summary>
/// <remarks>
/// <para>
/// Taken whole at work item 7 rather than split across work items, which is what ADR 0012's
/// second amendment records. Work item 7 is what makes ledger replay real, and a second
/// instance starting while the first holds a legitimate mute would replay that ledger,
/// restore the endpoint, and clear the entry - leaving the first instance believing it holds
/// a mute whose recovery record no longer exists. Any hard kill after that strands the
/// endpoint permanently. That is the ADR's own Context paragraph, and manual row E8 is its
/// test.
/// </para>
/// <para>
/// Acquisition of one mutex is a single invariant, so it has a single site and both entry
/// paths use it: the launch path through <see cref="StartupSequence"/>, and
/// <see cref="RestoreCommand"/>. Work item 12 still owns the tray balloon, the documented
/// exit-code UX, the shutdown channel and packaging - all of which need a tray.
/// </para>
/// <para>
/// Scoped by data root through <see cref="InstanceIdentity"/>, so two instances with
/// different roots do not contend. That is what keeps work item 6's two-instance test valid.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex? mutex, bool acquired)
    {
        _mutex = mutex;
        IsHeld = acquired;
    }

    /// <summary>Whether this process owns the guard.</summary>
    public bool IsHeld { get; }

    /// <summary>
    /// Attempts to become the single instance for one data root.
    /// </summary>
    /// <remarks>
    /// Uses the constructor's <c>createdNew</c> rather than <c>WaitOne(0)</c> deliberately.
    /// A mutex abandoned by a hard-killed instance makes <c>WaitOne</c> throw
    /// <see cref="AbandonedMutexException"/>, which would be reported as "another instance is
    /// running" when in fact nothing is - and that is exactly the state after the hard kill
    /// that ledger replay exists to repair.
    /// </remarks>
    public static SingleInstanceGuard TryAcquire(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string name = InstanceIdentity.ForRoot(root);

        try
        {
            var mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);

            if (createdNew)
            {
                return new SingleInstanceGuard(mutex, acquired: true);
            }

            mutex.Dispose();
            return new SingleInstanceGuard(null, acquired: false);
        }
        catch (UnauthorizedAccessException)
        {
            // The name exists and belongs to a session we cannot open. Somebody else holds
            // it, which is the same answer as contention.
            return new SingleInstanceGuard(null, acquired: false);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mutex is null)
        {
            return;
        }

        if (IsHeld)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
