using System.Runtime.InteropServices;

namespace DotMic.Setup;

// Notifications only wake the coordinator. Never enumerate, modify a key, wait,
// or release the final COM reference inside an IMMNotificationClient callback.
internal sealed class DeviceNotifications : IDisposable
{
    private readonly Enumerator enumerator;
    private readonly DeviceNotificationClient listener;
    private bool registered;
    internal DeviceNotifications(AutoResetEvent changed)
    {
        enumerator = (Enumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
        listener = new(changed);
        try { Marshal.ThrowExceptionForHR(enumerator.Register(listener)); registered = true; }
        catch { Marshal.FinalReleaseComObject(enumerator); throw; }
    }
    public void Dispose()
    {
        listener.Stop();
        if (registered) { enumerator.Unregister(listener); registered = false; Marshal.FinalReleaseComObject(enumerator); }
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Enumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out nint device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out nint device);
        [PreserveSig] int Register([MarshalAs(UnmanagedType.Interface)] DeviceNotification listener);
        [PreserveSig] int Unregister([MarshalAs(UnmanagedType.Interface)] DeviceNotification listener);
    }
}

// Top-level public COM types are required for a visible CCW; the host remains internal.
[ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface DeviceNotification
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, DevicePropertyKey key);
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct DevicePropertyKey { public Guid Format; public uint Id; }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class DeviceNotificationClient : DeviceNotification
    {
        private readonly AutoResetEvent changed;
        internal DeviceNotificationClient(AutoResetEvent changed) => this.changed = changed;
        private volatile bool stopped;
        internal void Stop() => stopped = true;
        private int Wake()
        {
            if (!stopped) { try { changed.Set(); } catch (ObjectDisposedException) { } }
            return 0;
        }
        public int OnDeviceStateChanged(string id, uint state) => Wake();
        public int OnDeviceAdded(string id) => Wake();
        public int OnDeviceRemoved(string id) => Wake();
        public int OnDefaultDeviceChanged(int flow, int role, string? id) => Wake();
        public int OnPropertyValueChanged(string id, DevicePropertyKey key) => Wake();
    }
