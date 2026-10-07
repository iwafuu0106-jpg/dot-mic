using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace DotMic.Setup;

internal static class Contract
{
    internal const string Version = "0.4.0-community", Clsid = "{8F611FC3-1A33-477D-9820-F8B9A11D1030}", Context = "{7DD39E65-9848-4CDE-AF2A-910E9D160376}";
    internal const string FxFormat = "{D04E05A6-594B-4FB6-A80D-01AF5EED7D1D}", ModeFormat = "{D3993A3F-99C2-4402-B5EC-A92A0367664B}";
    internal const string DefaultMode = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}", Microphone = "{DFF21BE1-F70F-11D0-B917-00A0C9223196}";
    internal const string AudioPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", ConfigPath = @"SOFTWARE\DOT MIC\Community";
    internal const string ComPath = @"SOFTWARE\Classes\CLSID\" + Clsid;
    internal const string ApoPath = AudioPath + @"\Plugins\AudioProcessingObjects\" + Clsid;
    internal const string ApoClassesPath = @"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\" + Clsid;
    internal static readonly string InstallRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DOT MIC", "Community", Version);
    internal static readonly string RecoveryRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DOT MIC", "Recovery");
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    internal static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}

internal sealed record EndpointIdentity(string EndpointId, string StableId, string ContainerId, string FriendlyName, string PhysicalInterface, string FxPath)
{
    public override string ToString() => FriendlyName + " · " + ContainerId;
    internal static EndpointIdentity Resolve(EndpointIdentity prior, IEnumerable<EndpointIdentity> endpoints)
    {
        var all = endpoints.ToArray();
        bool SameContainer(EndpointIdentity e) => string.IsNullOrEmpty(prior.ContainerId) || string.Equals(e.ContainerId, prior.ContainerId, StringComparison.OrdinalIgnoreCase);
        var matches = !string.IsNullOrEmpty(prior.StableId) ? all.Where(e => e.StableId == prior.StableId && SameContainer(e)).ToArray() : [];
        if (matches.Length == 0 && !string.IsNullOrEmpty(prior.ContainerId) && !string.IsNullOrEmpty(prior.PhysicalInterface)) matches = all.Where(e => SameContainer(e) && string.Equals(e.PhysicalInterface, prior.PhysicalInterface, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("対象マイクを一意に特定できません。マイクを再選択してください。");
        return matches[0];
    }
}
internal sealed record RawValue(string Name, uint Type, byte[] Data)
{
    internal bool Same(RawValue? other) => other != null && Type == other.Type && Data.AsSpan().SequenceEqual(other.Data);
    internal string Display => Type is 1 or 2 or 7 ? Encoding.Unicode.GetString(Data).TrimEnd('\0').Replace('\0', ' ') : Type == 4 && Data.Length == 4 ? BitConverter.ToUInt32(Data).ToString() : $"type {Type}, {Data.Length} bytes";
    internal static RawValue Text(string name, string text) => new(name, 1, Encoding.Unicode.GetBytes(text + "\0"));
    internal static RawValue Multi(string name, params string[] text) => new(name, 7, Encoding.Unicode.GetBytes(string.Join('\0', text) + "\0\0"));
    internal static RawValue Dword(string name, uint value) => new(name, 4, BitConverter.GetBytes(value));
}
internal sealed class KeyImage
{
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public byte[] Security { get; set; } = [];
    public uint SecurityMask { get; set; }
    public List<RawValue> Values { get; set; } = [];
    internal RawValue? Find(string name) => Values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
}
internal sealed class Edit
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public RawValue? Before { get; set; }
    public RawValue? After { get; set; }
    public bool Applied { get; set; }
}
internal sealed class FileEdit
{
    public string Relative { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool Existed { get; set; }
    public string? BeforeHash { get; set; }
    public bool Applied { get; set; }
    public string? StageHash { get; set; }
    public bool StageStarted { get; set; }
}
internal sealed class Receipt
{
    public int Schema { get; set; } = 1;
    public string Version { get; set; } = Contract.Version;
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Operation { get; set; } = "Install";
    public string Status { get; set; } = "Prepared";
    public EndpointIdentity Target { get; set; } = null!;
    public List<KeyImage> Keys { get; set; } = [];
    public List<Edit> Edits { get; set; } = [];
    public List<FileEdit> Files { get; set; } = [];
    public List<string> CreatedKeys { get; set; } = [];
    public List<string> AdvancedKeys { get; set; } = [];
    public List<string> PendingSecurityRestore { get; set; } = [];
    public Dictionary<string, byte[]> PendingSecurityExpected { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, byte[]> PendingSecurityOriginal { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool RegistrationPending { get; set; }
    public List<RawValue> RegistrationExpected { get; set; } = [];
    public bool ProtectedAudioWasAlreadyOne { get; set; }
    public string BootBefore { get; set; } = "";
    public List<string> Diagnostics { get; set; } = [];
    public bool AudioRestartConsent { get; set; }
    public bool ProtectedAudioConsent { get; set; }
    public bool ReplacementConsent { get; set; }
    public RawValue? WindowsEffectsDisabled { get; set; }
    public List<AudioDependent> AudioDependents { get; set; } = [];
    public bool DependentServiceConsent { get; set; }
    public bool AudioRestartPending { get; set; }
    public string? ArchivedOriginReceipt { get; set; }
    public ApplicationDeployment? Application { get; set; }
    public string? ApplicationRemovalJournal { get; set; }
    public bool ApplicationRemovalPending { get; set; }
}
internal sealed record PayloadFile(string Path, string Hash);
internal sealed record Payload(string Version, string ApoHash, List<PayloadFile> Files, List<PayloadFile>? ApplicationEntries = null);
internal sealed record AudioDependent(string Name, string DisplayName, uint OriginalState);
internal sealed class ApplicationDeployment
{
    public string Root { get; set; } = "";
    public List<FileEdit> Files { get; set; } = [];
    public bool DesktopShortcut { get; set; }
    public string? ShortcutBeforeHash { get; set; }
    public string? ShortcutHash { get; set; }
    public bool ShortcutApplied { get; set; }
    public bool CleanupPending { get; set; }
}
internal sealed record InstalledApplication(string Root, List<PayloadFile> Files, string? ShortcutHash, string? PackageRoot = null);
internal sealed record ApplicationRemoval(InstalledApplication Application, List<RawValue> OwnedConfiguration);
