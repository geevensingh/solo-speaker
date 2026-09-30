using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace SoloSpeaker.App.MuteActuator;

/// <summary>
/// §7.3's <c>IMMNotificationClient</c> subscription: tells the host when the default render
/// endpoint changes, so a headset swap re-targets and the previous endpoint is released.
/// </summary>
/// <remarks>
/// <para>
/// <b>Filtered to <c>(eRender, eMultimedia)</c>.</b> The notification fires for render and
/// capture across the console, multimedia and communications roles; any soft-phone moves the
/// communications role routinely. Acting on those would run a release-and-reacquire against
/// an endpoint that did not change - two ledger writes and a brief audible blip per event,
/// and Bluetooth reconnect storms make that rate unbounded.
/// </para>
/// <para>
/// <b>The callback body does nothing but hand off.</b> MMDevice requires that a notification
/// callback neither blocks nor re-enters the enumerator, and this one arrives on a COM pool
/// thread rather than the host's. It posts to the dispatch, which is asynchronous, so the
/// re-target runs on the one thread that is allowed to touch the cycle.
/// </para>
/// <para>
/// Registration is symmetric: the enumerator holds a reference until it is unregistered, so
/// <see cref="Dispose"/> is not optional, and <c>ShutdownSequence</c> stops the watch
/// <em>before</em> restoring endpoints - otherwise a headset unplugged mid-shutdown could
/// re-target into an endpoint whose ledger entry is about to be cleared.
/// </para>
/// </remarks>
public sealed class EndpointWatcher : IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly NotificationSink _sink;

    private bool _disposed;

    /// <summary>Subscribes to default-device changes.</summary>
    /// <param name="onDefaultRenderChanged">
    /// Invoked on a COM thread. Implementations must only queue work.
    /// </param>
    public EndpointWatcher(Action onDefaultRenderChanged)
    {
        ArgumentNullException.ThrowIfNull(onDefaultRenderChanged);

        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _sink = new NotificationSink(onDefaultRenderChanged);
        _enumerator.RegisterEndpointNotificationCallback(_sink);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(_sink);
        }
        catch (COMException)
        {
            // The enumerator is already gone; the registration went with it.
        }

        if (Marshal.IsComObject(_enumerator))
        {
            Marshal.ReleaseComObject(_enumerator);
        }
    }

    /// <summary>
    /// The managed COM callback. Reached through a classic COM-callable wrapper: CsWin32
    /// emits <c>[ComImport]</c> interfaces rather than source-generated ones, so
    /// <c>[GeneratedComClass]</c> does not apply here.
    /// </summary>
    private sealed class NotificationSink : IMMNotificationClient
    {
        private readonly Action _onDefaultRenderChanged;

        internal NotificationSink(Action onDefaultRenderChanged) =>
            _onDefaultRenderChanged = onDefaultRenderChanged;

        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR pwstrDefaultDeviceId)
        {
            if (flow == EDataFlow.eRender && role == ERole.eMultimedia)
            {
                _onDefaultRenderChanged();
            }
        }

        public void OnDeviceStateChanged(PCWSTR pwstrDeviceId, DEVICE_STATE dwNewState)
        {
        }

        public void OnDeviceAdded(PCWSTR pwstrDeviceId)
        {
        }

        public void OnDeviceRemoved(PCWSTR pwstrDeviceId)
        {
        }

        public void OnPropertyValueChanged(PCWSTR pwstrDeviceId, PROPERTYKEY key)
        {
        }
    }
}
