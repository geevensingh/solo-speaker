using System.Runtime.InteropServices;
using SoloSpeaker.Core.Abstractions;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;

namespace SoloSpeaker.App.MuteActuator;

/// <summary>
/// <see cref="IMuteActuator"/> over WASAPI - <c>docs/design.md</c> §7.3's
/// <c>IMMDeviceEnumerator</c> to <c>IAudioEndpointVolume::SetMute</c> path.
/// </summary>
/// <remarks>
/// <para>
/// Four operations and no policy: enumerate, read, set, set-by-id. Every rule about
/// <em>what</em> to do - the reconcile table, the ledger ordering, the self-change window -
/// lives in <c>SoloSpeaker.Core</c>'s <c>MuteReconciler</c>, where a <c>net10.0</c> test
/// project can reach it. This type is the part that genuinely cannot cross that line.
/// </para>
/// <para>
/// <b>Never <c>VK_VOLUME_MUTE</c>.</b> §7.3 is explicit: that is a toggle, and it drifts
/// permanently out of sync the first time anything else touches it. <c>SetMute</c> is
/// idempotent by construction, which is what makes the per-tick reconcile safe to run
/// forever.
/// </para>
/// </remarks>
public sealed class WasapiMuteActuator : IMuteActuator
{
    private readonly IMMDeviceEnumerator _enumerator;

    private IAudioEndpointVolume? _volume;
    private string? _endpointId;
    private bool _disposed;

    /// <summary>Creates an actuator over the system device enumerator.</summary>
    public WasapiMuteActuator()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        Retarget();
    }

    /// <inheritdoc/>
    public string? CurrentEndpointId
    {
        get
        {
            if (_disposed)
            {
                return null;
            }

            if (_volume is null)
            {
                // The default device may have appeared since the last attempt - a docking
                // station, a Bluetooth reconnect, or an endpoint re-enabled in Sound
                // settings. Retrying is what makes E7 recoverable rather than terminal.
                Retarget();
            }

            return _endpointId;
        }
    }

    /// <inheritdoc/>
    public bool? ReadActualMute()
    {
        if (_disposed || _volume is null)
        {
            return null;
        }

        try
        {
            return ReadMute(_volume);
        }
        catch (COMException)
        {
            // The endpoint went away between enumeration and the read. Null is the seam's
            // documented "cannot be read", and the reconciler turns it into an error cause
            // rather than into a claim.
            Release();
            return null;
        }
    }

    /// <inheritdoc/>
    public MuteApplyOutcome SetMute(bool muted)
    {
        if (_disposed)
        {
            return MuteApplyOutcome.Failed;
        }

        if (_volume is null)
        {
            Retarget();
        }

        if (_volume is null)
        {
            return MuteApplyOutcome.EndpointGone;
        }

        try
        {
            SetMuteOn(_volume, muted);
            return MuteApplyOutcome.Applied;
        }
        catch (COMException)
        {
            Release();
            return MuteApplyOutcome.Failed;
        }
    }

    /// <inheritdoc/>
    public MuteApplyOutcome TrySetMute(string endpointId, bool muted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        if (_disposed)
        {
            return MuteApplyOutcome.Failed;
        }

        IMMDevice? device = null;

        try
        {
            GetDeviceById(_enumerator, endpointId, out device);
        }
        catch (COMException)
        {
            // Genuinely absent from enumeration. §7.3 clears this entry without an error -
            // an endpoint that does not exist cannot be muted.
            return MuteApplyOutcome.EndpointGone;
        }

        if (device is null)
        {
            return MuteApplyOutcome.EndpointGone;
        }

        try
        {
            IAudioEndpointVolume volume = ActivateVolume(device);
            SetMuteOn(volume, muted);
            return MuteApplyOutcome.Applied;
        }
        catch (COMException)
        {
            // The endpoint exists and the write failed - access denied, an exclusive-mode
            // holder, a disabled device. Distinct from "gone" because the entry must be
            // RETAINED: something may still be muted and this is the only record of it.
            return MuteApplyOutcome.Failed;
        }
        finally
        {
            ReleaseComObject(device);
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
        Release();
        ReleaseComObject(_enumerator);
    }

    /// <summary>
    /// Points this actuator at the current default render endpoint, per §7.3's
    /// <c>(eRender, eMultimedia)</c> pair.
    /// </summary>
    public void Retarget()
    {
        Release();

        try
        {
            _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out IMMDevice device);

            try
            {
                _endpointId = ReadDeviceId(device);
                _volume = ActivateVolume(device);
            }
            finally
            {
                ReleaseComObject(device);
            }
        }
        catch (COMException)
        {
            // No default render endpoint. Matrix row E7 disables the device in Sound
            // settings; the reconciler raises the enumeration cause and takes no action.
            Release();
        }
    }

    private static unsafe bool ReadMute(IAudioEndpointVolume volume)
    {
        BOOL muted;
        volume.GetMute(&muted);
        return muted;
    }

    /// <summary>
    /// Applies a mute state. The event-context GUID is empty because this app is the only
    /// thing that needs to recognise its own writes, and it does so through the reconciler's
    /// §7.3 window rather than through a volume callback.
    /// </summary>
    private static unsafe void SetMuteOn(IAudioEndpointVolume volume, bool muted)
    {
        Guid context = Guid.Empty;
        volume.SetMute(muted, &context);
    }

    private static unsafe void GetDeviceById(IMMDeviceEnumerator enumerator, string endpointId, out IMMDevice device)
    {
        fixed (char* id = endpointId)
        {
            enumerator.GetDevice(id, out device);
        }
    }

    private static unsafe string ReadDeviceId(IMMDevice device)
    {
        PWSTR id = default;

        try
        {
            device.GetId(&id);
            return id.ToString();
        }
        finally
        {
            if (id.Value is not null)
            {
                // The callee allocated this with CoTaskMemAlloc and the contract puts the
                // free on us. Leaking it once per device change is unbounded over a session
                // of headset swaps.
                Marshal.FreeCoTaskMem((IntPtr)id.Value);
            }
        }
    }

    private static unsafe IAudioEndpointVolume ActivateVolume(IMMDevice device)
    {
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(&iid, 0, null, out object volume);
        return (IAudioEndpointVolume)volume;
    }

    private void Release()
    {
        if (_volume is not null)
        {
            ReleaseComObject(_volume);
            _volume = null;
        }

        _endpointId = null;
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.ReleaseComObject(instance);
        }
    }
}

/// <summary>
/// The <c>MMDeviceEnumerator</c> coclass.
/// </summary>
/// <remarks>
/// CsWin32 generates the interfaces but not an activatable coclass, so the CLSID is declared
/// here. This is the same shape the WASAPI documentation uses and needs no new dependency.
/// </remarks>
[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}
