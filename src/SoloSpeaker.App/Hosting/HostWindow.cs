using System.Collections.Concurrent;
using System.Windows.Forms;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace SoloSpeaker.App.Hosting;

/// <summary>
/// The host's message-only window: the one thread every mutation runs on, and the one
/// window procedure that receives Windows' lifecycle messages.
/// </summary>
/// <remarks>
/// <para>
/// There is one window rather than one per consumer because four work items need the same
/// pump and would otherwise each stand up their own. Work item 6 takes
/// <c>WM_ENDSESSION</c>; work item 9 adds <c>WM_HOTKEY</c>, which Windows delivers to a
/// <em>thread's</em> message queue and not to an arbitrary object; work item 11 adds the
/// power and session notifications; work item 12 adds the shutdown channel that
/// <c>install.ps1</c> needs in order to stop a running tray app. Putting the window inside
/// the type that happened to need it first - a departure announcer - is how the second
/// consumer ends up reaching into it.
/// </para>
/// <para>
/// It is message-only (<c>HWND_MESSAGE</c>), so it never appears on screen, in the taskbar,
/// or in Alt+Tab. That matters before work item 9: the host currently runs headless, and a
/// stray top-level window would be the only thing the user ever saw of it.
/// </para>
/// </remarks>
public sealed class HostWindow : NativeWindow, IDispatchTarget, IDisposable
{
    private const int WmEndSession = 0x0016;
    private const int WmApp = 0x8000;
    private const int WmDispatchWork = WmApp + 1;
    private const int HwndMessage = -3;

    private readonly ConcurrentQueue<Action> _queue = new();

    private bool _disposed;

    /// <summary>Creates the window on the calling thread, which becomes the dispatch thread.</summary>
    public HostWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "SoloSpeaker.Host",
            Parent = HwndMessage,
        });
    }

    /// <summary>
    /// Raised on <c>WM_ENDSESSION</c> - the phase the user can no longer cancel.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>WM_QUERYENDSESSION</c>, and deliberately not
    /// <c>SystemEvents.SessionEnding</c>, which is that query phase behind a friendlier name
    /// and exposes a <c>Cancel</c> flag to prove it. §7.1 requires the <c>bye</c> on
    /// <c>WM_ENDSESSION</c> "never on the query phase, which the user can still cancel", and
    /// manual matrix row A11 exists to catch exactly the machine that announces a departure
    /// it did not make: start a shutdown, cancel it, and the peer must not have been told.
    /// <c>SystemEvents.SessionEnded</c> is the right phase but arrives asynchronously on its
    /// own pump, which gives no guaranteed synchronous window in which to place ~150ms of
    /// departure sends before termination.
    /// </remarks>
    public event Action? SessionEnding;

    /// <inheritdoc />
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        _queue.Enqueue(work);

        if (Handle != IntPtr.Zero)
        {
            PInvoke.PostMessage((HWND)Handle, WmDispatchWork, default, default);
        }
    }

    /// <inheritdoc />
    public void Drain(TimeSpan timeout)
    {
        // Bounded so that a shutdown cannot hang on a queue that keeps refilling. Windows
        // gives a process a limited budget after WM_ENDSESSION and takes it away without
        // asking twice.
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (_queue.TryDequeue(out Action? work))
        {
            Run(work);

            if (Environment.TickCount64 >= deadline)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyHandle();
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1725:Parameter names should match base declaration",
        Justification = "The base declares this parameter as 'm'. AGENTS.md §4 forbids single-letter " +
            "names outside a short list of idioms that override parity is not on, and the parameter " +
            "name of an override is not part of any call site here - nothing calls WndProc by name.")]
    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case WmDispatchWork:
                if (_queue.TryDequeue(out Action? work))
                {
                    Run(work);
                }

                return;

            case WmEndSession:
                // wParam is FALSE when the session is not actually ending - another
                // application vetoed it after our WM_QUERYENDSESSION. Announcing a departure
                // then is the A11 failure.
                if (message.WParam != IntPtr.Zero)
                {
                    SessionEnding?.Invoke();
                }

                message.Result = IntPtr.Zero;
                return;

            default:
                base.WndProc(ref message);
                return;
        }
    }

    private static void Run(Action work)
    {
        try
        {
            work();
        }
        catch (Exception failure) when (failure is InvalidOperationException or ObjectDisposedException)
        {
            // The loop and the stores surface their own faults as reducer events, so
            // anything arriving here is a shutdown race rather than an arbitration outcome.
            // Wider exceptions are deliberately left to crash: AGENTS.md §4's rule is that a
            // silent catch means a machine is muted and nobody knows why.
        }
    }
}
