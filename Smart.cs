// MemControl's smart cleaning: what every process is doing (one Windows call, no process is opened), which programs
// have been idle, when automatic cleaning may run, whether a full-screen program is in front, the list of programs
// that are never cleaned, and how full the memory commit is.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

class ProcInfo
{
    public string Name = ""; // without ".exe", as Process.ProcessName gives it
    public int Pid, ParentPid, Handles, Threads;
    public long Created, CpuTicks, PrivateBytes, PrivateWorkingSet, WorkingSet;
}

static class Sys
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    /// <summary>
    /// Every process, from one NtQuerySystemInformation(SystemProcessInformation) call. It needs no administrator rights
    /// and covers Windows' own processes too. Empty when Windows refuses or is 32-bit (the offsets are for 64-bit).
    /// </summary>
    public static List<ProcInfo> Processes()
    {
        var list = new List<ProcInfo>();
        if (IntPtr.Size != 8) return list;
        int length = 1 << 20;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                int returned;
                int status = Native.NtQuerySystemInformation(5, buffer, length, out returned);
                if (status == unchecked((int)0xC0000004) && length < (1 << 28)) { length = Math.Max(length * 2, returned + 65536); continue; }
                if (status != 0) return list;
                IntPtr e = buffer;
                while (true)
                {
                    int next = Marshal.ReadInt32(e);
                    int pid = (int)Marshal.ReadInt64(e, 80);
                    IntPtr namePtr = Marshal.ReadIntPtr(e, 64);
                    string name = namePtr == IntPtr.Zero ? (pid == 0 ? "Idle" : "") : Marshal.PtrToStringUni(namePtr, (ushort)Marshal.ReadInt16(e, 56) / 2);
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                    list.Add(new ProcInfo {
                        Name = name, Pid = pid, ParentPid = (int)Marshal.ReadInt64(e, 88), Threads = Marshal.ReadInt32(e, 4), Handles = Marshal.ReadInt32(e, 96),
                        Created = Marshal.ReadInt64(e, 32), CpuTicks = Marshal.ReadInt64(e, 40) + Marshal.ReadInt64(e, 48),
                        PrivateWorkingSet = Marshal.ReadInt64(e, 8), WorkingSet = Marshal.ReadInt64(e, 144), PrivateBytes = Marshal.ReadInt64(e, 200),
                    });
                    if (next == 0) break;
                    e = e + next;
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    /// <summary>The process that owns the window in front, or 0.</summary>
    public static int ForegroundPid()
    {
        int pid;
        IntPtr window = GetForegroundWindow();
        return window != IntPtr.Zero && GetWindowThreadProcessId(window, out pid) != 0 ? pid : 0;
    }

    /// <summary>Windows' own answer: 2 = a full-screen program is in front, 3 = an exclusive full-screen Direct3D game, 4 = presentation mode, 5 = none of these.</summary>
    public static int NotificationState()
    {
        int state;
        return SHQueryUserNotificationState(out state) == 0 ? state : 0;
    }

    public static bool FullScreen()
    {
        int state = NotificationState();
        return state == 2 || state == 3 || state == 4;
    }

    /// <summary>The largest holders of committed memory, grouped by program: "game 18.9 GB, browser 5.3 GB (18)".</summary>
    public static string Largest(List<ProcInfo> processes, int count)
    {
        return string.Join(", ", processes.Where(p => p.Name != "").GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Name = g.Key, Bytes = g.Sum(p => p.PrivateBytes), Count = g.Count() })
            .OrderByDescending(g => g.Bytes).Take(count)
            .Select(g => g.Name + " " + (g.Bytes / 1073741824.0).ToString("0.0") + " GB" + (g.Count > 1 ? " (" + g.Count + ")" : "")));
    }
}

/// <summary>The "Never clean" list: program names without ".exe", compared without case.</summary>
static class ProgramList
{
    public static HashSet<string> Parse(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split(',', ';', '\n', '\r'))
        {
            string name = raw.Trim().Trim('"').Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            if (name != "") set.Add(name);
        }
        return set;
    }

    public static string Join(IEnumerable<string> names) { return string.Join(", ", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)); }
}

/// <summary>What a cleaning leaves alone.</summary>
class CleanOptions
{
    public readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<int> SkipPids = new HashSet<int>();
    public CleanOptions() { }
    public CleanOptions(IEnumerable<string> never) { foreach (var name in never) SkipNames.Add(name); }
    public bool Any { get { return SkipNames.Count > 0 || SkipPids.Count > 0; } }
    public bool Skips(ProcInfo p) { return SkipPids.Contains(p.Pid) || SkipNames.Contains(p.Name); }
}

/// <summary>
/// Which programs have been busy lately. A process is busy in a sample when it used at least 1 % of one processor
/// core since the previous sample; a process seen for the first time counts as busy.
/// </summary>
class Activity
{
    class Seen { public long Created, CpuTicks; public DateTime Busy; }
    readonly Dictionary<int, Seen> seen = new Dictionary<int, Seen>();
    DateTime last = DateTime.MinValue;

    public DateTime Last { get { return last; } }

    public void Sample(List<ProcInfo> processes, DateTime now)
    {
        double seconds = last == DateTime.MinValue ? 0 : (now - last).TotalSeconds;
        var alive = new HashSet<int>();
        foreach (var p in processes)
        {
            alive.Add(p.Pid);
            Seen s;
            if (!seen.TryGetValue(p.Pid, out s) || s.Created != p.Created) { seen[p.Pid] = new Seen { Created = p.Created, CpuTicks = p.CpuTicks, Busy = now }; continue; }
            if (seconds > 0 && p.CpuTicks - s.CpuTicks >= seconds * 1e7 * 0.01) s.Busy = now;
            s.CpuTicks = p.CpuTicks;
        }
        foreach (var gone in seen.Keys.Where(pid => !alive.Contains(pid)).ToList()) seen.Remove(gone);
        last = now;
    }

    /// <summary>True when the process is known and has not been busy for at least the given time.</summary>
    public bool Idle(ProcInfo p, TimeSpan forAtLeast, DateTime now)
    {
        Seen s;
        return seen.TryGetValue(p.Pid, out s) && s.Created == p.Created && now - s.Busy >= forAtLeast;
    }
}

/// <summary>
/// When the automatic "RAM use at or above N %" cleaning may run. Every automatic cleaning makes the next one wait
/// longer (5, 15, then 30 minutes), because programs take the memory straight back and cleaning in a loop only slows
/// the PC. Ten calm minutes under the limit start it over. A jump of 10 points above the last cleaning cuts the wait.
/// </summary>
class AutoPolicy
{
    public static readonly int[] WaitMinutes = { 2, 5, 15, 30 };
    int step, lastLoad = -1;
    DateTime last = DateTime.MinValue, below = DateTime.MinValue;

    /// <summary>Minutes that must pass after the last automatic cleaning.</summary>
    public int Wait { get { return WaitMinutes[step]; } }

    /// <summary>The earliest time of the next automatic cleaning.</summary>
    public DateTime Next { get { return last == DateTime.MinValue ? DateTime.MinValue : last.AddMinutes(Wait); } }

    /// <summary>Starts over, for example after the limit was changed.</summary>
    public void Reset() { step = 0; lastLoad = -1; below = DateTime.MinValue; }

    /// <summary>Is a cleaning due now, with RAM use at load % and the limit at limit %?</summary>
    public bool Due(DateTime now, int load, int limit)
    {
        if (load < limit)
        {
            if (below == DateTime.MinValue) below = now;
            if (now - below >= TimeSpan.FromMinutes(10)) { step = 0; lastLoad = -1; }
            return false;
        }
        below = DateTime.MinValue;
        if (last == DateTime.MinValue) return true;
        TimeSpan since = now - last;
        if (since < TimeSpan.FromMinutes(WaitMinutes[0])) return false;
        return since >= TimeSpan.FromMinutes(Wait) || (lastLoad >= 0 && load >= lastLoad + 10);
    }

    /// <summary>An automatic cleaning started at load % RAM use.</summary>
    public void Done(DateTime now, int load)
    {
        last = now;
        lastLoad = load;
        step = Math.Min(step + 1, WaitMinutes.Length - 1);
    }
}

/// <summary>
/// The commit charge (memory that programs have reserved) against the largest commit limit Windows can reach by
/// enlarging the page file.
/// </summary>
class CommitState
{
    public double CommitGb, LimitGb, CeilingGb;
    public bool CanGrow { get { return CeilingGb > LimitGb + 0.5; } }
    public int Percent { get { return CeilingGb > 0 ? (int)Math.Round(100 * CommitGb / CeilingGb) : 0; } }
}

static class PageFile
{
    const string Key = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    public static CommitState Read(Snapshot s)
    {
        var state = new CommitState { CommitGb = s.CommitGb, LimitGb = s.CommitLimitGb, CeilingGb = s.CommitLimitGb };
        try
        {
            double current = Math.Max(0, s.CommitLimitGb - s.TotalGb);
            double max = MaxGb(s.TotalGb, current, Registry.GetValue(Key, "PagingFiles", null) as string[], Volume);
            state.CeilingGb = Math.Max(s.CommitLimitGb, s.CommitLimitGb - current + max);
        }
        catch { }
        return state;
    }

    // { size, free space } of a volume in GB, or null.
    static double[] Volume(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? new[] { drive.TotalSize / 1073741824.0, drive.AvailableFreeSpace / 1073741824.0 } : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The largest total page-file size in GB under the present settings (the registry value PagingFiles). A page file
    /// managed by Windows grows to 3 × RAM or 4 GB, whichever is larger, but to no more than one eighth of its volume
    /// and no more than the free space there less 1 GB. A custom one grows to its set maximum. With no entry, the
    /// present size is taken as final.
    /// </summary>
    public static double MaxGb(double ramGb, double currentGb, string[] pagingFiles, Func<string, double[]> volume)
    {
        var entries = (pagingFiles ?? new string[0]).Select(e => e.Trim()).Where(e => e != "").ToList();
        if (entries.Count == 0) return currentGb;
        double total = 0, share = currentGb / entries.Count; // the present size, split evenly when there are several files
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        foreach (var entry in entries)
        {
            var parts = entry.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            double minMb = 0, maxMb = 0;
            bool sized = parts.Length >= 3
                && double.TryParse(parts[parts.Length - 2], NumberStyles.Float, CultureInfo.InvariantCulture, out minMb)
                && double.TryParse(parts[parts.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out maxMb);
            if (sized && maxMb > 0) { total += maxMb / 1024; continue; }
            string root = entry.StartsWith("?") || entry.Length < 3 || entry[1] != ':' ? systemRoot : entry.Substring(0, 3);
            double cap = Math.Max(3 * ramGb, 4);
            var v = volume(root);
            if (v != null) cap = Math.Min(Math.Min(cap, v[0] / 8), share + Math.Max(0, v[1] - 1));
            total += Math.Max(cap, share);
        }
        return Math.Max(total, currentGb);
    }
}
