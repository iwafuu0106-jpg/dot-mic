namespace DotMic.Setup;

internal static class OperationLock
{
    internal static IDisposable? TryAcquire()
    {
        SecureStorage.Directory(Contract.RecoveryRoot);
        string path = Path.Combine(Contract.RecoveryRoot, "setup-operation.lock");
        if (File.Exists(path)) SecureStorage.RecoveryFile(path);
        FileStream stream;
        try { stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
        try { SecureStorage.File(path); return stream; }
        catch { stream.Dispose(); throw; }
    }
}
