using Ocean.Core;
using Ocean.Core.Protocol;

namespace Ocean.Service.State;

public sealed class ItemOverride
{
    public ItemAccessMode Mode { get; set; } = ItemAccessMode.Inherit;
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
}

public sealed class ShareDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public AccessMode Mode { get; set; } = AccessMode.Public;
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
    public List<string> Roots { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, ItemOverride> Overrides { get; set; } = new();
    /// <summary>Owner's organization: item id → folder (only where it differs from the folder on disk).</summary>
    public Dictionary<string, string> ItemFolders { get; set; } = new();
    /// <summary>Folders the owner created or kept (so they exist even when empty).</summary>
    public List<string> Folders { get; set; } = new();
    /// <summary>Roots added "into" a folder: root path → folder; applied to their videos on the next scan.</summary>
    public Dictionary<string, string> PendingPlacement { get; set; } = new();
}

public sealed class ServiceConfig
{
    public string NodeName { get; set; } = Environment.MachineName;
    public string UserName { get; set; } = DefaultUser();
    public string Secret { get; set; } = "";
    public int LocalPort { get; set; } = OceanDefaults.LocalPort;
    public int PeerPort { get; set; } = OceanDefaults.PeerPort;
    public int DiscoveryPort { get; set; } = OceanDefaults.DiscoveryPort;
    public string DownloadDir { get; set; } = DefaultDownloadDir();
    public int UploadLimitKBps { get; set; }
    public bool SharingPaused { get; set; }
    public int NewDays { get; set; } = 3;
    public int MaxConcurrentDownloads { get; set; } = 2;
    public bool NotifyNewVideos { get; set; } = true;
    /// <summary>How long a node that went offline stays listed (with its last known shares).</summary>
    public int OfflineKeepHours { get; set; } = 24;
    /// <summary>Let other PCs connect on networks Windows marks as Public (the service manages the firewall rule).</summary>
    public bool AllowPublicNetworks { get; set; } = true;
    /// <summary>Carry traffic between PCs that have no network path to each other (see Ocean.Core.Protocol.Via).</summary>
    public bool AllowBridging { get; set; } = true;
    /// <summary>Make lighter copies of my heavy videos for watching (see Ocean.Service.Media.LightCopies).</summary>
    public bool LightCopies { get; set; } = true;
    public int LightKeepDays { get; set; } = 7;
    public int LightCacheGB { get; set; } = 20;
    /// <summary>"auto" | "classic" | "fast": how sockets are driven (see Ocean.Core.NetworkCompat).</summary>
    public string NetworkMode { get; set; } = "auto";
    /// <summary>Disk space for watched video bytes from other PCs (GB, 0 = off): replays and seeking back cost no network.</summary>
    public int StreamCacheGB { get; set; } = 5;
    public List<ShareDefinition> Shares { get; set; } = new();

    private static string DefaultUser()
    {
        var u = Environment.UserName;
        return string.IsNullOrWhiteSpace(u) || u.EndsWith("$") || u.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
            ? Environment.MachineName : u;
    }

    private static string DefaultDownloadDir()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos) || Environment.UserName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
            videos = Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos);
        if (string.IsNullOrWhiteSpace(videos)) videos = @"C:\Users\Public\Videos";
        return Path.Combine(videos, "Ocean");
    }
}

/// <summary>Thread-safe JSON-backed configuration with atomic writes.</summary>
public sealed class ConfigStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private ServiceConfig _current;

    public event Action<ServiceConfig>? Changed;

    public ConfigStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "config.json");
        _current = Load();
        // Environment overrides (used for multi-instance testing on one machine)
        _current.LocalPort = OceanDefaults.EnvPort("OCEAN_LOCAL_PORT", _current.LocalPort);
        _current.PeerPort = OceanDefaults.EnvPort("OCEAN_PEER_PORT", _current.PeerPort);
        _current.DiscoveryPort = OceanDefaults.EnvPort("OCEAN_DISCOVERY_PORT", _current.DiscoveryPort);
    }

    public ServiceConfig Current { get { lock (_lock) return _current; } }

    public void Update(Action<ServiceConfig> mutate)
    {
        ServiceConfig snapshot;
        lock (_lock)
        {
            mutate(_current);
            Save(_current);
            snapshot = _current;
        }
        Changed?.Invoke(snapshot);
    }

    private ServiceConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var cfg = Json.Deserialize<ServiceConfig>(File.ReadAllText(_path));
                if (cfg is not null) return cfg;
            }
        }
        catch { /* corrupt config: start fresh but keep a copy */ try { File.Copy(_path, _path + ".bad", true); } catch { } }
        var fresh = new ServiceConfig();
        Save(fresh);
        return fresh;
    }

    private void Save(ServiceConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Write(_path, System.Text.Json.JsonSerializer.Serialize(cfg, Json.Pretty));
    }
}

public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteBytes(string path, byte[] content)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
