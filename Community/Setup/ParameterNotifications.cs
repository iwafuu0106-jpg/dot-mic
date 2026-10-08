using System.Runtime.InteropServices;

namespace DotMic.Setup;

// A pending native notification is thread-agnostic. This watches parameter data
// only, without a registry polling timer or a writable manager command channel.
internal sealed class ParameterNotifications : IDisposable
{
    private nint key;
    private readonly AutoResetEvent changed;
    private bool armed;
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegOpenKeyEx(nint root, string path, uint options, uint access, out nint key);
    [DllImport("advapi32.dll")] private static extern int RegNotifyChangeKeyValue(nint key, bool subtree, uint filter, nint changed, bool asynchronous);
    [DllImport("advapi32.dll")] private static extern int RegCloseKey(nint key);
    internal ParameterNotifications(AutoResetEvent changed)
    {
        this.changed = changed;
        Open();
    }
    private void Open()
    {
        int error = RegOpenKeyEx(new(unchecked((int)0x80000002)), CommonProfile.PathName + @"\User", 0, 0x111, out key);
        if (error != 0) throw new System.ComponentModel.Win32Exception(error);
    }
    internal void Arm()
    {
        if (armed) return;
        if (key == 0) Open();
        int error = RegNotifyChangeKeyValue(key, false, 4 | 0x10000000, changed.SafeWaitHandle.DangerousGetHandle(), true);
        if (error == 1018) { // A privileged replacement invalidated the held key.
            RegCloseKey(key); key = 0; Open();
            error = RegNotifyChangeKeyValue(key, false, 4 | 0x10000000, changed.SafeWaitHandle.DangerousGetHandle(), true);
        }
        if (error != 0) throw new System.ComponentModel.Win32Exception(error);
        armed = true;
    }
    internal void Consumed() => armed = false;
    public void Dispose() { if (key != 0) { RegCloseKey(key); key = 0; } }
}
