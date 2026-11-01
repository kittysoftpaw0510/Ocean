using System.Diagnostics;
using Ocean.Service.State;

namespace Ocean.Service.Net;

/// <summary>
/// Keeps Windows Firewall open for Ocean on networks Windows marks as Public. Windows labels new and unrecognized
/// networks Public by default, and its firewall then blocks other PCs from connecting even on a home or office LAN:
/// discovery still works (so the node is listed) but nothing else does. The setup only allows Private and Domain
/// networks, so the service, which runs with system rights, manages the Public rule itself from a setting.
/// Uses netsh with exit codes only, so nothing depends on the language of its output.
/// </summary>
public sealed class FirewallRules : IHostedService
{
    /// <summary>Same name the setup uses, so install, uninstall and the service all manage the same rule.</summary>
    public const string PublicRuleName = "Ocean (public networks)";

    private readonly ConfigStore _config;
    private readonly ILogger<FirewallRules> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _exe = Environment.ProcessPath;
    private bool? _applied;

    public FirewallRules(ConfigStore config, ILogger<FirewallRules> log)
    {
        _config = config; _log = log;
    }

    /// <summary>Changing firewall rules needs administrator rights: true for the Windows service, false in portable mode.</summary>
    public bool CanManage => OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess && !string.IsNullOrEmpty(_exe);

    public Task StartAsync(CancellationToken ct)
    {
        _config.Changed += cfg => { if (cfg.AllowPublicNetworks != _applied) _ = ApplyAsync(cfg.AllowPublicNetworks); };
        _ = Task.Run(async () =>
        {
            // The setup's rule for private/domain networks: only PCs on the local subnet (older setups allowed any address).
            if (CanManage) await NetshAsync($"advfirewall firewall set rule name=\"Ocean\" dir=in program=\"{_exe}\" new remoteip=localsubnet");
            await ApplyAsync(_config.Current.AllowPublicNetworks);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task ApplyAsync(bool allow)
    {
        if (!CanManage) return;
        await _gate.WaitAsync();
        try
        {
            if (allow == _applied) return;
            var rule = $"name=\"{PublicRuleName}\" dir=in program=\"{_exe}\"";
            if (allow)
            {
                // Repair the existing rule (re-enable it, make sure it allows, only from the local subnet) or create it.
                var rc = await NetshAsync($"advfirewall firewall set rule {rule} new enable=yes action=allow profile=public remoteip=localsubnet");
                if (rc != 0)
                    rc = await NetshAsync($"advfirewall firewall add rule {rule} action=allow enable=yes profile=public remoteip=localsubnet " +
                                          "description=\"Ocean LAN video sharing on networks Windows marks as Public (Ocean settings: Network)\"");
                if (rc == 0) _log.LogInformation("firewall: other PCs may connect on Public networks too");
                else { _log.LogWarning("firewall: could not allow Ocean on Public networks (netsh exit {Code})", rc); return; }
            }
            else
            {
                // Exit code 1 just means there was no such rule.
                await NetshAsync($"advfirewall firewall delete rule {rule}");
                _log.LogInformation("firewall: Public networks closed to Ocean (private and domain networks only)");
            }
            _applied = allow;
        }
        catch (Exception ex) { _log.LogWarning(ex, "firewall: update failed"); }
        finally { _gate.Release(); }
    }

    private static async Task<int> NetshAsync(string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), args)
        {
            // Output is NOT redirected: only the exit code matters. Redirecting it and disposing the process while a
            // read on its output pipe was still completing crashed the whole service on some PCs
            // ("'overlapped' has already been freed", an unhandled exception on an I/O completion thread).
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("netsh did not start");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(); } catch { } return -1; }
        return p.ExitCode;
    }
}
