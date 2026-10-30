using System.Diagnostics;
using System.Net.Http;
using System.ServiceProcess;
using Ocean.Core;
using Ocean.Core.Protocol;

namespace Ocean.App.Services;

/// <summary>
/// Finds the running Ocean service. When the Windows service is installed (normal setup) the app waits for it
/// and starts it if needed; otherwise (portable use) it starts Ocean.Service.exe as a hidden background process.
/// </summary>
public static class ServiceConnector
{
    public static int LocalPort => OceanDefaults.EnvPort("OCEAN_LOCAL_PORT", OceanDefaults.LocalPort);

    /// <summary>Status of the installed Windows service, or null when it is not installed.</summary>
    public static ServiceControllerStatus? InstalledServiceStatus()
    {
        try
        {
            using var sc = new ServiceController(OceanDefaults.ServiceName);
            return sc.Status;
        }
        catch { return null; } // not installed
    }

    public static async Task<(ApiClient Api, NodeStatusDto Status, bool Spawned)> ConnectAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Connecting to the Ocean service…");
        var existing = await TryConnectAsync(ct);
        if (existing is not null) return (existing.Value.Api, existing.Value.Status, false);

        // Portable/dev runs (OCEAN_DATA_DIR set) always use their own background process.
        var portable = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OCEAN_DATA_DIR"));
        var installed = portable ? null : InstalledServiceStatus();
        if (installed is not null)
        {
            progress?.Report("Waiting for the Ocean service…");
            if (installed is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused)
            {
                // Setup grants signed-in users the right to start (not stop) the service.
                try { using var sc = new ServiceController(OceanDefaults.ServiceName); sc.Start(); } catch { }
            }
            var r = await WaitAsync(TimeSpan.FromSeconds(90), ct);
            if (r is not null) return (r.Value.Api, r.Value.Status, false);
            throw new InvalidOperationException(
                "The Ocean service is installed but is not answering.\n\n" +
                "Open Services (services.msc), start “Ocean LAN Video Sharing”, then open Ocean again. " +
                "If it keeps stopping, see the logs in " + Path.Combine(OceanDefaults.DataDir, "logs") + ".");
        }

        progress?.Report("Starting the Ocean background service…");
        var exe = Path.Combine(AppContext.BaseDirectory, "Ocean.Service.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("Ocean.Service.exe was not found next to the application and no running service answered.", exe);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        try { Process.Start(psi); }
        catch (Exception ex) { throw new InvalidOperationException("Could not start Ocean.Service.exe: " + ex.Message, ex); }

        var spawned = await WaitAsync(TimeSpan.FromSeconds(25), ct);
        if (spawned is not null) return (spawned.Value.Api, spawned.Value.Status, true);
        throw new System.TimeoutException("The Ocean service did not start. Check the logs in " + Path.Combine(OceanDefaults.DataDir, "logs"));
    }

    private static async Task<(ApiClient Api, NodeStatusDto Status)?> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            var r = await TryConnectAsync(ct);
            if (r is not null) return r;
        }
        return null;
    }

    public static async Task<(ApiClient Api, NodeStatusDto Status)?> TryConnectAsync(CancellationToken ct)
    {
        string token;
        try { token = File.Exists(OceanDefaults.LocalTokenPath) ? File.ReadAllText(OceanDefaults.LocalTokenPath).Trim() : ""; }
        catch { token = ""; }
        if (token.Length == 0) return null;
        var api = new ApiClient(LocalPort, token);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var status = await api.GetAsync<NodeStatusDto>("/api/status", cts.Token);
            return status is null ? null : (api, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ApiException)
        {
            return null;
        }
    }
}
