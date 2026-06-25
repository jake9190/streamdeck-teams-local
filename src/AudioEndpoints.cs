using System.Runtime.InteropServices;

namespace MsTeamsLocal;

/// <summary>A Windows audio endpoint (microphone or speaker).</summary>
public sealed record AudioEndpoint(string Id, string Name, bool IsCapture);

/// <summary>
/// Enumerates active Windows audio endpoints via Core Audio. An endpoint's <see cref="AudioEndpoint.Id"/>
/// is the same string Teams uses as the AutomationId for that device in its audio options panel, so a
/// stored Id can be matched exactly at selection time (independent of display language).
/// </summary>
public static class AudioEndpoints
{
    public static IReadOnlyList<AudioEndpoint> Enumerate()
    {
        var result = new List<AudioEndpoint>();
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Collect(enumerator, EDataFlow.eRender, result);   // speakers
            Collect(enumerator, EDataFlow.eCapture, result);  // microphones
        }
        catch (Exception ex) { Log.Error("audio endpoint enumeration failed", ex); }
        finally { if (enumerator is not null) Marshal.FinalReleaseComObject(enumerator); }
        Log.Info($"enumerated {result.Count} active audio endpoints");
        return result;
    }

    private static void Collect(IMMDeviceEnumerator enumerator, EDataFlow flow, List<AudioEndpoint> into)
    {
        if (enumerator.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out var collection) != 0 || collection is null)
            return;
        try
        {
            collection.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(i, out device) != 0 || device is null) continue;
                    if (device.GetId(out var id) != 0 || string.IsNullOrEmpty(id)) continue;
                    string name = GetFriendlyName(device) ?? id;
                    into.Add(new AudioEndpoint(id, name, flow == EDataFlow.eCapture));
                }
                catch { }
                finally { if (device is not null) Marshal.FinalReleaseComObject(device); }
            }
        }
        finally { Marshal.FinalReleaseComObject(collection); }
    }

    private static string? GetFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(STGM_READ, out var store) != 0 || store is null) return null;
        try
        {
            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.GetString(); }
            finally { PropVariantClear(ref pv); }
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    private const int DEVICE_STATE_ACTIVE = 0x1;
    private const int STGM_READ = 0x0;

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14,
    };

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pvar);
}

internal enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IMMDeviceCollection devices);
    // Remaining methods (GetDefaultAudioEndpoint, ...) are unused and intentionally omitted.
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    [PreserveSig] int Commit();
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY { public Guid fmtid; public int pid; }

[StructLayout(LayoutKind.Explicit)]
internal struct PROPVARIANT
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr pointerValue;

    private const ushort VT_LPWSTR = 31;

    public readonly string? GetString()
        => vt == VT_LPWSTR ? Marshal.PtrToStringUni(pointerValue) : null;
}
