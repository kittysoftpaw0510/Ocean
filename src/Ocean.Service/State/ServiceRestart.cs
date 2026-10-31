using System.Diagnostics;
using Ocean.Core;

namespace Ocean.Service.State;

/// <summary>
/// Lets the Windows service restart itself: the service manager's recovery actions (set up by the installer) restart
/// a service that stops with a failure, but only count a non-zero exit code as a failure when "failureflag" is set.
/// The service sets that flag at startup, so a deliberate exit with <see cref="RestartExitCode"/> comes back a few
/// seconds later, the same way it does after a crash.
/// </summary>
public static class ServiceRestart
{
    public const int RestartExitCode = 3;

    public static void AllowRestartOnExit(ILogger log)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), $"failureflag {OceanDefaults.ServiceName} 1")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(10_000);
        }
        catch (Exception ex) { log.LogDebug(ex, "sc failureflag"); }
    }

    public static void ExitForRestart() => Environment.Exit(RestartExitCode);
}
