using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ocean.Service.State;

/// <summary>
/// Keeps Ocean's CPU use under a share of the PC (Settings, 25 % by default), enforced by Windows: the service runs in
/// a job object with a hard CPU cap, and everything it starts (ffmpeg) joins that job, so the total never goes
/// above the cap however many cores the PC has. ffmpeg also gets a nested job capped at two thirds of the budget,
/// so streaming and browsing always keep a share. Both jobs end their processes when the service exits, so no
/// ffmpeg is ever left running.
/// <para>The app (its player) is not limited: watching a video takes what it needs, mostly from the graphics chip.</para>
/// </summary>
public static class CpuLimit
{
    public const int DefaultPercent = 25;
    /// <summary>ffmpeg's share of the budget.</summary>
    private const double FfmpegShare = 2.0 / 3;

    private static readonly object Lock = new();
    private static IntPtr _job;
    private static int _percent = 100;

    /// <summary>The cap in force (100 = none).</summary>
    public static int Percent => Volatile.Read(ref _percent);

    /// <summary>Puts this process in the capped job (once) and sets the cap; 100 lifts it. Returns false if Windows refused.</summary>
    public static bool Apply(int percent, ILogger log)
    {
        percent = Math.Clamp(percent, 5, 100);
        lock (Lock)
        {
            try
            {
                if (_job == IntPtr.Zero)
                {
                    var job = NewJob();
                    if (!AssignProcessToJobObject(job, GetCurrentProcess())) throw new Win32Exception(Marshal.GetLastWin32Error());
                    _job = job;
                }
                SetRate(_job, percent);
                _percent = percent;
                log.LogInformation("CPU limit: Ocean uses at most {Pct}% of this PC ({Cores} cores){Ffmpeg}", percent, Environment.ProcessorCount,
                    percent < 100 ? $", lighter copies at most {FfmpegPercent(percent)}%" : "");
                return true;
            }
            catch (Exception ex)
            {
                log.LogWarning("CPU limit: Windows refused it ({Msg}); Ocean runs without a cap", ex.Message);
                return false;
            }
        }
    }

    /// <summary>ffmpeg's cap within the budget.</summary>
    public static int FfmpegPercent(int percent) => Math.Max(3, (int)Math.Round(Math.Min(percent, 100) * FfmpegShare));

    /// <summary>How many threads ffmpeg should use for its share: more would only wait for their turn (and take memory).</summary>
    public static int FfmpegThreads()
    {
        var share = Percent >= 100 ? 1.0 : FfmpegPercent(Percent) / 100.0;
        return Math.Clamp((int)Math.Ceiling(Environment.ProcessorCount * share), 2, 16);
    }

    /// <summary>
    /// Caps a child process (ffmpeg) at its share of the budget, in a job nested in Ocean's; the returned handle must
    /// stay open while it runs (closing it ends the process).
    /// </summary>
    public static SafeJobHandle? CapChild(System.Diagnostics.Process child, ILogger log)
    {
        if (Percent >= 100) return null;
        try
        {
            var job = NewJob();
            if (!AssignProcessToJobObject(job, child.Handle)) { var err = Marshal.GetLastWin32Error(); CloseHandle(job); throw new Win32Exception(err); }
            // a nested job's cap is a share of its parent's budget, not of the whole PC (measured: 17 % gave 17 % of 25 %)
            SetRate(job, (int)Math.Round(FfmpegShare * 100));
            return new SafeJobHandle(job);
        }
        catch (Exception ex)
        {
            log.LogDebug("CPU limit for {Name}: {Msg} (it stays within Ocean's own cap)", child.ProcessName, ex.Message);
            return null;
        }
    }

    public sealed class SafeJobHandle : IDisposable
    {
        private IntPtr _h;
        public SafeJobHandle(IntPtr h) { _h = h; }
        public void Dispose() { var h = Interlocked.Exchange(ref _h, IntPtr.Zero); if (h != IntPtr.Zero) CloseHandle(h); }
    }

    private static IntPtr NewJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        // the job's processes end with it (the service exiting never leaves ffmpeg behind)
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var err = Marshal.GetLastWin32Error(); CloseHandle(job); throw new Win32Exception(err);
        }
        return job;
    }

    private static void SetRate(IntPtr job, int percent)
    {
        var rate = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = percent >= 100 ? 0u : JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
            CpuRate = (uint)(Math.Clamp(percent, 1, 100) * 100), // in 1/100 of a percent of the whole PC
        };
        if (!SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref rate, (uint)Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    // ------------------------------------------------------------------ Win32

    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION { public uint ControlFlags; public uint CpuRate; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION info, uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
