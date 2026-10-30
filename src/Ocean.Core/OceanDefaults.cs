namespace Ocean.Core;

/// <summary>Well-known constants and data locations shared by the service and the UI.</summary>
public static class OceanDefaults
{
    public const int ProtocolVersion = 1;
    public const int LocalPort = 38470;      // loopback HTTP API consumed by the UI
    public const int PeerPort = 38471;       // LAN HTTPS (mutual TLS) API consumed by other nodes
    public const int DiscoveryPort = 38472;  // UDP beacon port
    public const string ServiceName = "OceanService";
    public const string AppVersion = "1.0.2";

    /// <summary>Data directory. Override with OCEAN_DATA_DIR (handy for running two nodes on one PC).</summary>
    public static string DataDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("OCEAN_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Ocean");
        }
    }

    public static int EnvPort(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var p) && p is > 0 and < 65536 ? p : fallback;

    public static string LocalTokenPath => Path.Combine(DataDir, "local.token");
}
