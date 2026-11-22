// MemControl: a tray RAM cleaner in the spirit of Mem Reduct (henrypp/memreduct), plus what it does not have:
// a kernel-leak watch (some drivers leak kernel pool on every process start, e.g. file objects; no cleaner can
// free that, only a Restart), the top memory users and a 60-minute graph.
// Cleaning: process working sets, the system file cache, the modified page list, the standby list (low priority or
// all), page combining and the registry cache (NtSetSystemInformation, the same native calls Mem Reduct uses).
// Every area except the working sets of your own programs needs administrator rights.
// Smart automatic cleaning and the commit warning are in Smart.cs, "Always run as administrator" in AdminTask.cs.
// Build: build.cmd (the .NET Framework compiler built into Windows). Settings: memcontrol.ini beside the exe.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    // "/show" opens the window (a running MemControl opens its own); "/processes" opens the process manager;
    // "/clean" cleans once, writes the result to memcontrol.log and exits (no window, no tray).
    // "/takeover" replaces the running copy; "/admintask ..." is the one-time setup of "Always run as administrator".
    [STAThread]
    static int Main(string[] args)
    {
        Func<string, bool> has = flag => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        int named = Array.FindIndex(args, a => a.Equals("/instance", StringComparison.OrdinalIgnoreCase));
        if (named >= 0 && named + 1 < args.Length) Instance = args[named + 1];
        bool show = has("/show"), processes = has("/processes"), takeover = has("/takeover");
        if (has("/clean"))
        {
            var settings = Settings.Load();
            var r = Cleaner.Clean(settings.Areas, new CleanOptions(settings.Exclude));
            Log.Write("clean (/clean): " + r.Summary());
            return r.Errors.Count == 0 ? 0 : 1;
        }
        int setup = Array.FindIndex(args, a => a.Equals("/admintask", StringComparison.OrdinalIgnoreCase));
        if (setup >= 0) return AdminTask.Command(args.Skip(setup + 1).ToArray());

        bool created;
        using (var once = Shared.OpenMutex("Local\\MemControl" + Instance, out created))
        using (var ask = Shared.OpenEvent("Local\\MemControlShow" + Instance))
        using (var askProcesses = Shared.OpenEvent("Local\\MemControlProcesses" + Instance))
        using (var quit = Shared.OpenEvent("Local\\MemControlQuit" + Instance))
        {
            // "/takeover": this copy replaces the running one. It asks that copy to close and waits for it.
            if (!created && takeover)
            {
                quit.Set();
                try { created = once.WaitOne(15000); }
                catch (AbandonedMutexException) { created = true; } // it exited while holding the mutex: ours now
                quit.Reset(); // if nobody was left to hear the request, this copy must not hear it itself
                Log.Write("takeover: " + (created ? "the earlier copy closed, running now" : "the earlier copy did not close within 15 s"));
            }
            if (!created) { if (show) ask.Set(); if (processes) askProcesses.Set(); return 0; }

            // "Always run as administrator": a copy started without the rights hands over to the one its task starts.
            if (!takeover && !Native.IsAdmin() && Settings.Load().AlwaysAdmin)
            {
                var task = AdminTask.State();
                if (task == AdminTask.Status.Ready && HandOver(once)) { if (show) ask.Set(); if (processes) askProcesses.Set(); return 0; }
                Log.Write("always run as administrator: " + (task == AdminTask.Status.Ready ? "the elevated copy did not start"
                    : task == AdminTask.Status.Missing ? "its task is missing"
                    : task == AdminTask.Status.OtherCopy ? "its task starts another copy or is disabled" : "its task cannot be read")
                    + "; running without the rights (tick the box again to repair)");
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var tray = new Tray(ask, askProcesses, quit, show);
            if (processes) tray.ShowProcesses(null);
            Application.Run(tray);
        }
        return 0;
    }

    // Starts the elevated copy through the task, lets go of the single-instance mutex and waits until that copy holds it.
    // False when nobody came within 15 s: this copy then holds the mutex again and runs as it is.
    static bool HandOver(Mutex once)
    {
        if (!AdminTask.Run()) return false;
        once.ReleaseMutex();
        var until = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            Thread.Sleep(200);
            bool mine;
            try { mine = once.WaitOne(0); }
            catch (AbandonedMutexException) { mine = true; }
            if (!mine) return true;
            if (DateTime.UtcNow >= until) return false;
            once.ReleaseMutex();
        }
    }

    public static string Folder { get { return AppDomain.CurrentDomain.BaseDirectory; } }
    public static string Exe { get { return Application.ExecutablePath; } }

    // For tests only ("/instance NAME" or the MEMCONTROL_INSTANCE variable): separate single-instance and task names,
    // so a test never meets your running copy.
    public static string Instance = Environment.GetEnvironmentVariable("MEMCONTROL_INSTANCE") ?? "";
    public static string InstanceArgs { get { return Instance == "" ? "" : " /instance " + Instance; } }
}

[Flags]
enum Area
{
    None = 0,
    WorkingSets = 1,      // every process's working set (admin: all; otherwise your own programs)
    FileCache = 2,        // the system file cache working set
    ModifiedList = 4,     // modified pages written out and freed
    StandbyLow = 8,       // standby list, lowest priority only (gentle)
    StandbyAll = 16,      // the whole standby list (strong: Windows rebuilds its cache afterwards)
    Combine = 32,         // page combining (identical pages merged)
    Registry = 64,        // registry cache written out
}

static class Log
{
    public static void Write(string line)
    {
        try
        {
            string file = Path.Combine(Program.Folder, "memcontrol.log");
            if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) File.Delete(file);
            File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
        }
        catch { }
    }
}

class Settings
{
    public Area Areas = Area.WorkingSets | Area.FileCache | Area.ModifiedList | Area.StandbyLow | Area.Registry;
    public bool AutoOnPercent = true;
    public int AutoPercent = 90;
    public bool AutoEvery;
    public int EveryMinutes = 30;
    public bool Notify = true;
    public bool Hotkey = true;
    public double KernelYellowGb = 4, KernelRedGb = 8;
    public bool Gentle = true;            // automatic cleaning leaves busy programs and the one in front alone
    public bool PauseFullScreen = true;   // no automatic cleaning while a full-screen program (a game) is in front
    public HashSet<string> Exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // "Never clean": program names
    public bool CommitWarn = true;
    public int CommitYellow = 85, CommitRed = 93; // % of the largest commit limit Windows can reach
    public bool AlwaysAdmin;              // a copy started without administrator rights restarts itself through the task

    static string File_ { get { return Path.Combine(Program.Folder, "memcontrol.ini"); } }

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (!File.Exists(File_)) return s;
            foreach (var raw in File.ReadAllLines(File_))
            {
                int eq = raw.IndexOf('=');
                if (eq < 1) continue;
                string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1).Trim();
                int n; double d;
                switch (k)
                {
                    case "areas": if (int.TryParse(v, out n)) s.Areas = (Area)n; break;
                    case "autoOnPercent": s.AutoOnPercent = v == "1"; break;
                    case "autoPercent": if (int.TryParse(v, out n)) s.AutoPercent = Math.Max(50, Math.Min(99, n)); break;
                    case "autoEvery": s.AutoEvery = v == "1"; break;
                    case "everyMinutes": if (int.TryParse(v, out n)) s.EveryMinutes = Math.Max(5, Math.Min(720, n)); break;
                    case "notify": s.Notify = v == "1"; break;
                    case "hotkey": s.Hotkey = v == "1"; break;
                    case "kernelYellowGb": if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) s.KernelYellowGb = d; break;
                    case "kernelRedGb": if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) s.KernelRedGb = d; break;
                    case "gentle": s.Gentle = v == "1"; break;
                    case "pauseFullScreen": s.PauseFullScreen = v == "1"; break;
                    case "never": s.Exclude = ProgramList.Parse(v); break;
                    case "commitWarn": s.CommitWarn = v == "1"; break;
                    case "commitYellow": if (int.TryParse(v, out n)) s.CommitYellow = Math.Max(50, Math.Min(99, n)); break;
                    case "commitRed": if (int.TryParse(v, out n)) s.CommitRed = Math.Max(50, Math.Min(100, n)); break;
                    case "alwaysAdmin": s.AlwaysAdmin = v == "1"; break;
                }
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            File.WriteAllLines(File_, new[] {
                "areas=" + (int)Areas, "autoOnPercent=" + (AutoOnPercent ? 1 : 0), "autoPercent=" + AutoPercent,
                "autoEvery=" + (AutoEvery ? 1 : 0), "everyMinutes=" + EveryMinutes, "notify=" + (Notify ? 1 : 0),
                "hotkey=" + (Hotkey ? 1 : 0), "kernelYellowGb=" + KernelYellowGb.ToString(inv), "kernelRedGb=" + KernelRedGb.ToString(inv),
                "gentle=" + (Gentle ? 1 : 0), "pauseFullScreen=" + (PauseFullScreen ? 1 : 0), "never=" + ProgramList.Join(Exclude),
                "commitWarn=" + (CommitWarn ? 1 : 0), "commitYellow=" + CommitYellow, "commitRed=" + CommitRed, "alwaysAdmin=" + (AlwaysAdmin ? 1 : 0),
            });
        }
        catch { }
    }
}

struct Snapshot
{
    public double TotalGb, AvailGb, CommitGb, CommitLimitGb, KernelGb, CacheGb, PagedGb, NonpagedGb;
    // Kernel memory that each leaked file object pins (the object, its I/O extensions, its name and the filter
    // manager's name record): about 1.2 KB, measured on a leaking display driver (2 file objects per process start).
    public const double LeakBytesPerFileObject = 1200;
    public double LeakGb { get { return StuckFiles > 0 ? StuckFiles * LeakBytesPerFileObject / (1L << 30) : 0; } }
    public int LoadPercent;
    public long StuckFiles;
    public DateTime At;
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PerfInfo
    {
        public int cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public int HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)] public static extern bool GetPerformanceInfo(out PerfInfo info, int size);
    [DllImport("psapi.dll", SetLastError = true)] public static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);
    [DllImport("ntdll.dll")] public static extern int NtSetSystemInformation(int infoClass, IntPtr buffer, int length);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetSystemFileCacheSize(IntPtr min, IntPtr max, int flags);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool LookupPrivilegeValue(string system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct TokenPrivileges { public int Count; public long Luid; public int Attributes; }

    public static bool EnablePrivilege(string name)
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out token)) return false; // ADJUST_PRIVILEGES | QUERY
        try
        {
            long luid;
            if (!LookupPrivilegeValue(null, name, out luid)) return false;
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 }; // SE_PRIVILEGE_ENABLED
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0; // ERROR_NOT_ALL_ASSIGNED otherwise
        }
        finally { CloseHandle(token); }
    }

    public static bool IsAdmin()
    {
        using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static Snapshot Read()
    {
        var s = new Snapshot { At = DateTime.Now, StuckFiles = -1 };
        PerfInfo p;
        if (GetPerformanceInfo(out p, Marshal.SizeOf(typeof(PerfInfo))))
        {
            double page = p.PageSize.ToUInt64(), gb = 1L << 30;
            s.TotalGb = p.PhysicalTotal.ToUInt64() * page / gb;
            s.AvailGb = p.PhysicalAvailable.ToUInt64() * page / gb;
            s.CommitGb = p.CommitTotal.ToUInt64() * page / gb;
            s.CommitLimitGb = p.CommitLimit.ToUInt64() * page / gb;
            s.PagedGb = p.KernelPaged.ToUInt64() * page / gb;
            s.NonpagedGb = p.KernelNonpaged.ToUInt64() * page / gb;
            s.KernelGb = s.PagedGb + s.NonpagedGb;
            s.CacheGb = p.SystemCache.ToUInt64() * page / gb;
            s.LoadPercent = s.TotalGb > 0 ? (int)Math.Round(100 * (1 - s.AvailGb / s.TotalGb)) : 0;
        }
        try { s.StuckFiles = LiveFileObjects(); } catch { }
        return s;
    }

    // SystemPoolTagInformation (22): live "File" pool objects (allocations - frees), the kernel-leak signature.
    static long LiveFileObjects()
    {
        int length = 1 << 20;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                int returned;
                int status = NtQuerySystemInformation(22, buffer, length, out returned);
                if (status == unchecked((int)0xC0000004)) { length *= 2; continue; }
                if (status != 0 || IntPtr.Size != 8) return -1;
                int count = Marshal.ReadInt32(buffer);
                for (int i = 0; i < count; i++)
                {
                    IntPtr e = buffer + 8 + i * 40;
                    if (Marshal.ReadInt32(e) != 0x656C6946) continue; // "File"
                    long np = (long)Marshal.ReadInt32(e, 24) - Marshal.ReadInt32(e, 28), pg = (long)Marshal.ReadInt32(e, 4) - Marshal.ReadInt32(e, 8);
                    return (uint)np + (uint)pg;
                }
                return -1;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}

class CleanResult
{
    public double BeforeGb, AfterGb;
    public List<string> Done = new List<string>(), Skipped = new List<string>(), Errors = new List<string>();
    public double FreedGb { get { return Math.Max(0, AfterGb - BeforeGb); } }
    public string Summary()
    {
        string s = string.Format("freed {0:0.00} GB (available {1:0.0} -> {2:0.0} GB); done: {3}", FreedGb, BeforeGb, AfterGb, Done.Count > 0 ? string.Join(", ", Done) : "nothing");
        if (Skipped.Count > 0) s += "; skipped without administrator rights: " + string.Join(", ", Skipped);
        if (Errors.Count > 0) s += "; failed: " + string.Join(", ", Errors);
        return s;
    }
}

static class Cleaner
{
    const int SystemMemoryListInformation = 80, SystemCombinePhysicalMemoryInformation = 130, SystemRegistryReconciliationInformation = 155;
    const int MemoryEmptyWorkingSets = 2, MemoryFlushModifiedList = 3, MemoryPurgeStandbyList = 4, MemoryPurgeLowPriorityStandbyList = 5;

    public static readonly Dictionary<Area, string> Names = new Dictionary<Area, string> {
        { Area.WorkingSets, "Working sets of programs" }, { Area.FileCache, "System file cache" },
        { Area.ModifiedList, "Modified page list" }, { Area.StandbyLow, "Standby list (low priority)" },
        { Area.StandbyAll, "Standby list (all)" }, { Area.Combine, "Combine identical pages" },
        { Area.Registry, "Registry cache" },
    };

    static readonly object gate = new object();

    public static CleanResult Clean(Area areas, CleanOptions options = null)
    {
        lock (gate)
        {
            var r = new CleanResult { BeforeGb = Native.Read().AvailGb };
            bool admin = Native.IsAdmin();
            bool profile = admin && Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
            bool quota = admin && Native.EnablePrivilege("SeIncreaseQuotaPrivilege");

            if ((areas & Area.WorkingSets) != 0)
            {
                // With administrator rights and nothing to leave alone, one Windows call empties every working set.
                // Otherwise one process at a time, so that some can be skipped.
                bool leave = options != null && options.Any;
                if (!leave && profile && MemoryList(MemoryEmptyWorkingSets)) r.Done.Add(Names[Area.WorkingSets]);
                else
                {
                    if (admin) Native.EnablePrivilege("SeDebugPrivilege"); // to open services and other accounts' programs
                    int left;
                    int trimmed = TrimProcesses(Candidates(), options, out left);
                    if (trimmed > 0 || left > 0) r.Done.Add(Names[Area.WorkingSets] + " (" + trimmed + (trimmed == 1 ? " process" : " processes") + (admin ? "" : ", yours only") + (left > 0 ? ", " + left + " left alone" : "") + ")");
                    else r.Errors.Add(Names[Area.WorkingSets]);
                }
            }
            if ((areas & Area.FileCache) != 0)
            {
                if (!quota) r.Skipped.Add(Names[Area.FileCache]);
                else if (Native.SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0)) r.Done.Add(Names[Area.FileCache]);
                else r.Errors.Add(Names[Area.FileCache]);
            }
            Step(r, areas, Area.ModifiedList, profile, () => MemoryList(MemoryFlushModifiedList));
            if ((areas & Area.StandbyAll) != 0) Step(r, areas, Area.StandbyAll, profile, () => MemoryList(MemoryPurgeStandbyList));
            else Step(r, areas, Area.StandbyLow, profile, () => MemoryList(MemoryPurgeLowPriorityStandbyList));
            Step(r, areas, Area.Combine, profile, CombinePages);
            Step(r, areas, Area.Registry, admin, () => NtStatusOk(Native.NtSetSystemInformation(SystemRegistryReconciliationInformation, IntPtr.Zero, 0)));

            Thread.Sleep(400);
            r.AfterGb = Native.Read().AvailGb;
            return r;
        }
    }

    static void Step(CleanResult r, Area areas, Area area, bool allowed, Func<bool> act)
    {
        if ((areas & area) == 0) return;
        if (!allowed) { r.Skipped.Add(Names[area]); return; }
        bool ok;
        try { ok = act(); } catch { ok = false; }
        (ok ? r.Done : r.Errors).Add(Names[area]);
    }

    static bool NtStatusOk(int status) { return status >= 0; }

    static bool MemoryList(int command)
    {
        IntPtr p = Marshal.AllocHGlobal(4);
        try { Marshal.WriteInt32(p, command); return NtStatusOk(Native.NtSetSystemInformation(SystemMemoryListInformation, p, 4)); }
        finally { Marshal.FreeHGlobal(p); }
    }

    static bool CombinePages()
    {
        // MEMORY_COMBINE_INFORMATION_EX { HANDLE Handle; ULONG_PTR PagesCombined; ULONG Flags; } (Windows 10+)
        int size = IntPtr.Size * 2 + 8;
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            return NtStatusOk(Native.NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, p, size));
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    static List<ProcInfo> Candidates()
    {
        var all = Sys.Processes();
        if (all.Count > 0) return all;
        foreach (var p in Process.GetProcesses())
        {
            try { all.Add(new ProcInfo { Pid = p.Id, Name = p.ProcessName }); }
            catch { }
            finally { p.Dispose(); }
        }
        return all;
    }

    /// <summary>
    /// Empties the working set of each given process that this account may open (PROCESS_QUERY_LIMITED_INFORMATION |
    /// PROCESS_SET_QUOTA), except those the options leave alone. Returns how many were emptied.
    /// </summary>
    public static int TrimProcesses(IEnumerable<ProcInfo> processes, CleanOptions options, out int left)
    {
        left = 0;
        int done = 0, self = Process.GetCurrentProcess().Id;
        foreach (var p in processes)
        {
            if (p.Pid == 0 || p.Pid == 4 || p.Pid == self) continue;
            if (options != null && options.Skips(p)) { left++; continue; }
            IntPtr h = Native.OpenProcess(0x1000 | 0x0100, false, p.Pid);
            if (h == IntPtr.Zero) continue;
            try { if (Native.EmptyWorkingSet(h)) done++; } finally { Native.CloseHandle(h); }
        }
        return done;
    }
}

class Tray : ApplicationContext
{
    readonly NotifyIcon icon = new NotifyIcon();
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer(), askTimer = new System.Windows.Forms.Timer();
    readonly HotkeyWindow hotkeys = new HotkeyWindow();
    public readonly Settings Settings = Settings.Load();
    public readonly List<Snapshot> History = new List<Snapshot>();
    public readonly List<string> Recent = new List<string>();
    public Snapshot Last, First;
    DateTime lastAuto = DateTime.MinValue, lastKernelWarning = DateTime.MinValue, lastHistory = DateTime.MinValue;
    int kernelLevel = -1;
    IntPtr iconHandle = IntPtr.Zero;
    MainWindow window;
    bool cleaning;
    readonly Activity activity = new Activity();
    public readonly AutoPolicy Policy = new AutoPolicy();
    public List<ProcInfo> Processes = new List<ProcInfo>();
    public CommitState Commit = new CommitState();
    public string AutoStatus = "", Largest = "";
    DateTime lastSample = DateTime.MinValue, lastCeiling = DateTime.MinValue, lastCommitWarning = DateTime.MinValue, handOverAt = DateTime.MinValue;
    double ceilingGb;
    int commitLevel = -1;

    public Tray(EventWaitHandle ask, EventWaitHandle askProcesses, EventWaitHandle quit, bool show)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (o, e) => ShowWindow());
        menu.Items.Add("Clean now", null, (o, e) => CleanNow("manually"));
        menu.Items.Add("Processes…", null, (o, e) => ShowProcesses(null));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart computer…", null, (o, e) => AskRestart());
        menu.Items.Add("Exit", null, (o, e) => Quit());
        icon.ContextMenuStrip = menu;
        icon.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        icon.MouseDoubleClick += (o, e) => { if (e.Button == MouseButtons.Left) CleanNow("double-click"); };
        icon.BalloonTipClicked += (o, e) => ShowWindow();
        icon.Visible = true;
        hotkeys.Pressed += () => CleanNow("Ctrl+Shift+F1");
        ApplyHotkey();
        askTimer.Interval = 500;
        askTimer.Tick += (o, e) =>
        {
            // Another copy takes over (Run as administrator): have it reopen the window if it is open here, then close.
            if (quit.WaitOne(0)) { if (window != null && !window.IsDisposed && window.Visible) ask.Set(); Quit(); return; }
            if (ask.WaitOne(0)) ShowWindow();
            if (askProcesses.WaitOne(0)) ShowProcesses(null);
        };
        askTimer.Start();
        tick.Interval = 2000;
        tick.Tick += (o, e) => Update();
        tick.Start();
        Update();
        if (show) ShowWindow();
    }

    public void ApplyHotkey()
    {
        Native.UnregisterHotKey(hotkeys.Handle, 1);
        if (Settings.Hotkey && !Native.RegisterHotKey(hotkeys.Handle, 1, 0x2 | 0x4, (int)Keys.F1)) Log.Write("Ctrl+Shift+F1 is already taken by another program");
    }

    void Update()
    {
        Last = Native.Read();
        if (First.At == default(DateTime)) First = Last;
        if (Last.At - lastHistory >= TimeSpan.FromSeconds(10))
        {
            History.Add(Last);
            lastHistory = Last.At;
            if (History.Count > 360) History.RemoveAt(0); // 60 min
        }
        if (Last.At - lastSample >= TimeSpan.FromSeconds(10)) Sample();
        if (Last.At - lastCeiling >= TimeSpan.FromSeconds(30)) { lastCeiling = Last.At; ceilingGb = PageFile.Read(Last).CeilingGb; }
        Commit = new CommitState { CommitGb = Last.CommitGb, LimitGb = Last.CommitLimitGb, CeilingGb = Math.Max(ceilingGb, Last.CommitLimitGb) };
        if (handOverAt != DateTime.MinValue && Last.At - handOverAt > TimeSpan.FromSeconds(10))
        {
            handOverAt = DateTime.MinValue;
            Log.Write("run as administrator: the task did not start the elevated copy, asking with a prompt");
            ElevateWithPrompt();
        }
        SetIcon();
        icon.Text = Clip(string.Format("RAM {0}% · free {1:0.0} GB · kernel {2:0.0} GB", Last.LoadPercent, Last.AvailGb, Last.KernelGb));
        AutoClean();
        KernelWatch();
        CommitWatch();
        if (window != null && !window.IsDisposed && window.Visible) window.Refresh(this);
    }

    // Every 10 s: what each process has used since the last look. It feeds "busy lately" and the largest holders of commit.
    void Sample()
    {
        lastSample = DateTime.Now;
        try
        {
            var all = Sys.Processes();
            if (all.Count == 0) return;
            activity.Sample(all, lastSample);
            Processes = all;
            Largest = Sys.Largest(all, 3);
        }
        catch { }
    }

    // Automatic cleaning: never within 2 minutes of the last one, not while a full-screen program is in front, and by
    // RAM use only when AutoPolicy says so (it waits longer and longer while cleaning does not keep RAM under the limit).
    void AutoClean()
    {
        AutoStatus = "";
        if (cleaning || !(Settings.AutoOnPercent || Settings.AutoEvery)) return;
        var now = DateTime.Now;
        if (Settings.PauseFullScreen && Sys.FullScreen()) { AutoStatus = "paused: a full-screen program is in front"; return; }
        bool tooSoon = now - lastAuto < TimeSpan.FromMinutes(2);
        if (!tooSoon && Settings.AutoOnPercent && Policy.Due(now, Last.LoadPercent, Settings.AutoPercent))
        {
            lastAuto = now;
            Policy.Done(now, Last.LoadPercent);
            CleanNow("auto: RAM " + Last.LoadPercent + "%", true, "next no sooner than " + Policy.Next.ToString("HH:mm"));
        }
        else if (!tooSoon && Settings.AutoEvery && now - lastAuto >= TimeSpan.FromMinutes(Settings.EveryMinutes)
            && (lastAuto != DateTime.MinValue || now - First.At >= TimeSpan.FromMinutes(Settings.EveryMinutes)))
        { lastAuto = now; CleanNow("auto every " + Settings.EveryMinutes + " min", true); }
        else if (Settings.AutoOnPercent && Last.LoadPercent >= Settings.AutoPercent)
        {
            DateTime floor = lastAuto.AddMinutes(2), next = Policy.Next > floor ? Policy.Next : floor;
            AutoStatus = "waits until " + next.ToString("HH:mm") + ": RAM stays above the limit";
        }
    }

    // Commit: memory that programs have reserved, against the largest limit Windows can reach. Cleaning cannot lower it.
    // The level rises at once and falls only 3 points under a mark, so the warning does not repeat at the edge.
    void CommitWatch()
    {
        if (!Settings.CommitWarn) { commitLevel = 0; return; }
        int pct = Commit.Percent, yellow = Settings.CommitYellow, red = Settings.CommitRed;
        int up = pct >= red ? 2 : pct >= yellow ? 1 : 0, held = pct >= red - 3 ? 2 : pct >= yellow - 3 ? 1 : 0;
        int level = Math.Max(up, Math.Min(Math.Max(commitLevel, 0), held));
        string size = string.Format("{0:0.0} of {1:0.0} GB", Commit.CommitGb, Commit.CeilingGb);
        if (level == 1 && commitLevel < 1)
            icon.ShowBalloonTip(15000, "Memory commit is " + pct + "% full", "Programs have reserved " + size + ". Largest: " + Largest + ".", ToolTipIcon.Info);
        if (level == 2 && (commitLevel < 2 || DateTime.Now - lastCommitWarning > TimeSpan.FromMinutes(30)))
        {
            icon.ShowBalloonTip(20000, "Memory commit is " + pct + "% full", "Close a large program, or programs may fail for lack of memory. Largest: " + Largest + ".", ToolTipIcon.Warning);
            lastCommitWarning = DateTime.Now;
        }
        if (level != commitLevel && commitLevel >= 0) Log.Write("commit " + pct + "% (" + size + "), level " + level + "; largest: " + Largest);
        commitLevel = level;
    }

    // What a cleaning leaves alone. Always: the "Never clean" list. With a full-screen program in front: that program.
    // In automatic cleaning with "Gentle": also the program in front and every program that was busy in the last 3 minutes.
    CleanOptions Options(bool auto)
    {
        var options = new CleanOptions(Settings.Exclude);
        bool gentle = auto && Settings.Gentle;
        if (!gentle && !Sys.FullScreen()) return options;
        if (DateTime.Now - lastSample > TimeSpan.FromSeconds(3)) Sample();
        int front = Sys.ForegroundPid(), self = Process.GetCurrentProcess().Id;
        var inFront = Processes.FirstOrDefault(p => p.Pid == front);
        if (inFront != null && front != self && front > 4 && inFront.Name != "") options.SkipNames.Add(inFront.Name);
        if (gentle)
        {
            var now = DateTime.Now;
            foreach (var p in Processes) if (!activity.Idle(p, TimeSpan.FromMinutes(3), now)) options.SkipPids.Add(p.Pid);
        }
        return options;
    }

    void KernelWatch()
    {
        int level = Last.KernelGb >= Settings.KernelRedGb ? 2 : Last.KernelGb >= Settings.KernelYellowGb ? 1 : 0;
        if (level == 1 && kernelLevel < 1)
            icon.ShowBalloonTip(15000, "Kernel is holding " + Last.KernelGb.ToString("0.0") + " GB",
                "Cleaning cannot free this (a driver leak). Restart when convenient.", ToolTipIcon.Info);
        if (level == 2 && (kernelLevel < 2 || DateTime.Now - lastKernelWarning > TimeSpan.FromHours(3)))
        {
            icon.ShowBalloonTip(20000, "Kernel is holding " + Last.KernelGb.ToString("0.0") + " GB",
                "The computer may slow down. Save your work and Restart (not Shut down).", ToolTipIcon.Warning);
            lastKernelWarning = DateTime.Now;
        }
        kernelLevel = level;
    }

    /// <summary>Cleans in the background. An automatic cleaning is gentler (see Options); note is added to its log line.</summary>
    public void CleanNow(string why, bool auto = false, string note = null)
    {
        if (cleaning) return;
        cleaning = true;
        var areas = Settings.Areas;
        var options = Options(auto);
        var worker = new BackgroundWorker();
        CleanResult result = null;
        worker.DoWork += (o, e) => result = Cleaner.Clean(areas, options);
        worker.RunWorkerCompleted += (o, e) =>
        {
            cleaning = false;
            string summary = (result != null ? result.Summary() : "failed") + (note != null ? "; " + note : "");
            Recent.Insert(0, DateTime.Now.ToString("HH:mm") + " " + why + ": " + summary);
            if (Recent.Count > 20) Recent.RemoveAt(Recent.Count - 1);
            Log.Write(why + ": " + summary);
            if (Settings.Notify && result != null && (!auto || result.FreedGb * 1024 >= 100)) // an automatic cleaning that freed next to nothing stays quiet
            {
                string body = result.Skipped.Count > 0 ? "Some areas need administrator rights: open MemControl and choose Run as administrator." : "Available now: " + result.AfterGb.ToString("0.0") + " GB.";
                icon.ShowBalloonTip(6000, "Freed " + (result.FreedGb * 1024).ToString("0") + " MB", body, ToolTipIcon.Info);
            }
            Update();
        };
        worker.RunWorkerAsync();
    }

    public bool Cleaning { get { return cleaning; } }

    public void AskRestart()
    {
        if (MessageBox.Show("The computer will restart in 10 seconds.\n\nSave your work and close your programs first.\n\nRestart now?",
            "MemControl", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10 /c \"MemControl: restarting to free memory held by the kernel.\"") { UseShellExecute = false, CreateNoWindow = true });
    }

    public void RestartAsAdmin()
    {
        // Once "Always run as administrator" is set up, its task starts the elevated copy without a prompt, and that
        // copy asks this one to close. If it has not come within 10 s, Update falls back to the prompt.
        if (AdminTask.State() == AdminTask.Status.Ready && AdminTask.Run()) { handOverAt = DateTime.Now; return; }
        ElevateWithPrompt();
    }

    void ElevateWithPrompt()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Program.Exe, "/show /takeover" + Program.InstanceArgs) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Program.Folder });
            Quit();
        }
        catch (Win32Exception) { /* the UAC window was declined: keep running as is */ }
    }

    /// <summary>Turns "Always run as administrator" on or off. Returns null, or why it could not be turned on.</summary>
    public string SetAlwaysAdmin(bool on)
    {
        if (!on)
        {
            Settings.AlwaysAdmin = false;
            Settings.Save();
            if (Native.IsAdmin()) AdminTask.Delete(AdminTask.Name); // without the rights the task stays; unused, it does nothing
            Log.Write("always run as administrator: off");
            return null;
        }
        if (AdminTask.State() != AdminTask.Status.Ready)
        {
            string sid = AdminTask.UserSid();
            string why = Native.IsAdmin() ? AdminTask.Create(AdminTask.Name, Program.Exe, AdminTask.Arguments, sid, true) : AdminTask.Elevated("create " + sid);
            if (why == null && AdminTask.State() != AdminTask.Status.Ready) why = "the task was not created (see memcontrol.log)";
            if (why != null) return why;
        }
        Settings.AlwaysAdmin = true;
        Settings.Save();
        Log.Write("always run as administrator: on");
        if (!Native.IsAdmin()) RestartAsAdmin();
        return null;
    }

    public void Quit() { icon.Visible = false; Native.UnregisterHotKey(hotkeys.Handle, 1); ExitThread(); }

    ProcessWindow processes;
    public void ShowProcesses(string filter)
    {
        if (processes == null || processes.IsDisposed) processes = new ProcessWindow(this);
        processes.Open(filter);
    }

    void ShowWindow()
    {
        if (window == null || window.IsDisposed) window = new MainWindow(this);
        window.Refresh(this);
        window.Show();
        window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    void SetIcon()
    {
        int pct = Last.LoadPercent;
        int commit = Settings.CommitWarn ? Commit.Percent : 0;
        Color color = Last.KernelGb >= Settings.KernelRedGb || pct >= 90 || commit >= Settings.CommitRed ? Color.FromArgb(220, 53, 69)
            : Last.KernelGb >= Settings.KernelYellowGb || pct >= 75 || commit >= Settings.CommitYellow ? Color.FromArgb(230, 162, 30) : Color.FromArgb(40, 167, 90);
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(color))
        using (var font = new Font("Segoe UI", pct >= 100 ? 12f : 17f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var path = Rounded(new Rectangle(0, 0, 31, 31), 7)) g.FillPath(brush, path);
            g.DrawString(pct.ToString(), font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
            IntPtr handle = bmp.GetHicon();
            icon.Icon = Icon.FromHandle(handle);
            if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
            iconHandle = handle;
        }
    }

    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
        p.AddArc(r.Right - radius * 2, r.Y, radius * 2, radius * 2, 270, 90);
        p.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        p.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    // NotifyIcon.Text takes at most 63 characters on .NET Framework.
    static string Clip(string text) { return text.Length > 63 ? text.Substring(0, 63) : text; }
}

class HotkeyWindow : NativeWindow
{
    public event Action Pressed;
    public HotkeyWindow() { CreateHandle(new CreateParams()); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && Pressed != null) Pressed(); // WM_HOTKEY
        base.WndProc(ref m);
    }
}

class MainWindow : Form
{
    readonly Tray tray;
    readonly Label ram = new Label(), ramSub = new Label(), commit = new Label(), kernel = new Label(), kernelSub = new Label(), admin = new Label(), autoStatus = new Label();
    readonly Bar ramBar = new Bar();
    readonly Graph graph = new Graph();
    readonly ListView top = new ListView(), recent = new ListView();
    readonly Button clean = new Button(), elevate = new Button();
    readonly Dictionary<Area, CheckBox> boxes = new Dictionary<Area, CheckBox>();
    readonly CheckBox autoPct = new CheckBox(), autoEvery = new CheckBox(), notify = new CheckBox(), hotkey = new CheckBox(),
        gentle = new CheckBox(), pause = new CheckBox(), commitWarn = new CheckBox(), always = new CheckBox();
    readonly TextBox never = new TextBox();
    readonly NumericUpDown pct = new NumericUpDown(), every = new NumericUpDown();
    readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000 };
    readonly bool canAlways = AdminTask.Possible();
    DateTime lastTop = DateTime.MinValue;
    bool loading;

    public MainWindow(Tray tray)
    {
        this.tray = tray;
        Text = "MemControl";
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 680);
        BackColor = Color.FromArgb(248, 249, 251);

        // Left column: RAM, kernel, graph, top programs.
        var head = Label("Memory (RAM)", 16, 12, true);
        ram.SetBounds(16, 34, 360, 40); ram.Font = new Font("Segoe UI", 22f, FontStyle.Bold);
        ramBar.SetBounds(16, 78, 360, 14);
        ramSub.SetBounds(16, 96, 360, 18);
        commit.SetBounds(16, 116, 304, 20);
        var commitWhat = new LinkLabel { Text = "Explain", AutoSize = true, Left = 326, Top = 116 };
        commitWhat.LinkClicked += (o, e) => CommitHelp.Show(this, tray);
        var kHead = Label("Windows kernel", 16, 142, true);
        kernel.SetBounds(16, 162, 360, 24); kernel.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        kernelSub.SetBounds(16, 188, 360, 74); kernelSub.ForeColor = Color.DimGray;
        var gHead = Label("Last 60 minutes", 16, 266, true);
        graph.SetBounds(16, 286, 360, 90);
        var tHead = Label("Top memory users", 16, 382, true);
        top.SetBounds(16, 402, 360, 264); top.View = View.Details; top.FullRowSelect = true; top.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        top.Columns.Add("Program", 168); top.Columns.Add("Processes", 74, HorizontalAlignment.Right); top.Columns.Add("MB", 90, HorizontalAlignment.Right);
        // Double-click a program: the process manager, filtered to it.
        top.DoubleClick += (o, e) => { if (top.SelectedItems.Count > 0) tray.ShowProcesses(top.SelectedItems[0].Text); };
        var manage = new LinkLabel { Text = "Manage processes…", AutoSize = true, Left = 250, Top = 383 };
        manage.LinkClicked += (o, e) => tray.ShowProcesses(null);
        Controls.Add(manage);

        // Right column: clean, areas, automatic, recent cleanings.
        int x = 396;
        clean.SetBounds(x, 14, 348, 44); clean.Text = "Clean now"; clean.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        clean.BackColor = Color.FromArgb(40, 120, 220); clean.ForeColor = Color.White; clean.FlatStyle = FlatStyle.Flat; clean.FlatAppearance.BorderSize = 0;
        clean.Click += (o, e) => { tray.CleanNow("manually"); clean.Enabled = false; };
        admin.SetBounds(x, 62, 348, 36); admin.ForeColor = Color.DimGray;
        elevate.SetBounds(x, 98, 164, 28); elevate.Text = "Run as administrator";
        elevate.Click += (o, e) => tray.RestartAsAdmin();
        always.SetBounds(x + 172, 100, 176, 24); always.Enabled = canAlways;
        tips.SetToolTip(always, canAlways
            ? "Windows asks once. After that MemControl gets administrator rights at every start, through a scheduled task named MemControl.\nAny program running under your account could use that task to get the same rights."
            : "This Windows account is not an administrator, so there is no consent to remember.");
        always.CheckedChanged += (o, e) =>
        {
            if (loading) return;
            string why = tray.SetAlwaysAdmin(always.Checked);
            if (why != null) MessageBox.Show(this, "\"Always run as administrator\" was not turned on: " + why + ".", "MemControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
            loading = true; always.Checked = tray.Settings.AlwaysAdmin; loading = false;
        };
        var aHead = Label("What to clean", x, 136, true);
        int y = 156;
        foreach (var kv in Cleaner.Names)
        {
            var cb = new CheckBox { Text = kv.Value, Left = x, Top = y, Width = 348, Height = 22 };
            Area a = kv.Key;
            cb.CheckedChanged += (o, e) => { if (loading) return; if (cb.Checked) tray.Settings.Areas |= a; else tray.Settings.Areas &= ~a; if (a == Area.StandbyAll && cb.Checked) boxes[Area.StandbyLow].Checked = false; tray.Settings.Save(); };
            boxes[a] = cb; Controls.Add(cb); y += 22;
        }
        var tip = Label("\"Standby list (all)\" frees the most, but Windows then rebuilds its cache (files open a bit slower for a while).", x, y + 2, false);
        tip.AutoSize = false; tip.Size = new Size(348, 34); tip.ForeColor = Color.DimGray; tip.Font = new Font("Segoe UI", 8.5f);
        y += 40;
        var autoHead = Label("Automatic", x, y, true);
        autoStatus.SetBounds(x + 80, y + 2, 268, 18); autoStatus.ForeColor = Color.DimGray; autoStatus.Font = new Font("Segoe UI", 8.5f); autoStatus.TextAlign = ContentAlignment.TopRight;
        y += 20;
        autoPct.SetBounds(x, y, 210, 24); autoPct.Text = "Clean when RAM use ≥"; pct.SetBounds(x + 214, y, 52, 24); pct.Minimum = 50; pct.Maximum = 99;
        var pctUnit = Label("%", x + 270, y + 3, false); y += 28;
        autoEvery.SetBounds(x, y, 210, 24); autoEvery.Text = "Clean every"; every.SetBounds(x + 214, y, 52, 24); every.Minimum = 5; every.Maximum = 720;
        var everyUnit = Label("min", x + 270, y + 3, false); y += 28;
        gentle.SetBounds(x, y, 348, 22); gentle.Text = "Gentle: skip busy programs and the one in front"; y += 22;
        tips.SetToolTip(gentle, "Automatic cleaning then empties only programs that have done nothing for 3 minutes.\nPrograms you are using would take their memory straight back, which only slows them down.");
        pause.SetBounds(x, y, 348, 22); pause.Text = "Pause during games and full-screen programs"; y += 24;
        tips.SetToolTip(pause, "No automatic cleaning while a full-screen program is in front.\nCleaning by hand (Ctrl+Shift+F1) still works and leaves that program alone.");
        var neverHead = Label("Never clean:", x, y + 3, false); never.SetBounds(x + 88, y, 260, 24); y += 28;
        tips.SetToolTip(never, "Program names separated by commas, for example: game, obs64\nOr right-click a program in the process manager.");
        never.Leave += (o, e) => SaveNever();
        commitWarn.SetBounds(x, y, 348, 22); commitWarn.Text = "Warn when memory commit is almost full"; y += 22;
        notify.SetBounds(x, y, 348, 22); notify.Text = "Notify how much was freed"; y += 22;
        hotkey.SetBounds(x, y, 348, 22); hotkey.Text = "Clean with Ctrl+Shift+F1"; y += 28;
        EventHandler save = (o, e) =>
        {
            if (loading) return;
            var s = tray.Settings;
            if (s.AutoPercent != (int)pct.Value || s.AutoOnPercent != autoPct.Checked) tray.Policy.Reset(); // a new limit acts at once
            s.AutoOnPercent = autoPct.Checked; s.AutoPercent = (int)pct.Value; s.AutoEvery = autoEvery.Checked; s.EveryMinutes = (int)every.Value;
            s.Gentle = gentle.Checked; s.PauseFullScreen = pause.Checked; s.CommitWarn = commitWarn.Checked;
            s.Notify = notify.Checked; bool hk = s.Hotkey != hotkey.Checked; s.Hotkey = hotkey.Checked; s.Save();
            if (hk) tray.ApplyHotkey();
        };
        autoPct.CheckedChanged += save; pct.ValueChanged += save; autoEvery.CheckedChanged += save; every.ValueChanged += save; notify.CheckedChanged += save; hotkey.CheckedChanged += save;
        gentle.CheckedChanged += save; pause.CheckedChanged += save; commitWarn.CheckedChanged += save;
        var rHead = Label("Recent cleanings", x, y, true); y += 20;
        recent.SetBounds(x, y, 348, 666 - y); recent.View = View.Details; recent.HeaderStyle = ColumnHeaderStyle.None; recent.ShowItemToolTips = true; recent.Columns.Add("", 330);
        var restart = new LinkLabel { Text = "Restart computer…", Left = 16, Top = 0, AutoSize = true };
        restart.LinkClicked += (o, e) => tray.AskRestart();
        kernelSub.Controls.Add(restart); restart.Top = 54;
        var explain = new LinkLabel { Text = "What is this? Why does it grow?", AutoSize = true, Left = 150, Top = 54 };
        explain.LinkClicked += (o, e) => KernelHelp.Show(this, tray.Last);
        kernelSub.Controls.Add(explain);

        Controls.AddRange(new Control[] { head, ram, ramBar, ramSub, commit, commitWhat, kHead, kernel, kernelSub, gHead, graph, tHead, top, clean, admin, elevate, always, aHead, tip, autoHead, autoStatus, autoPct, pct, pctUnit, autoEvery, every, everyUnit, gentle, pause, neverHead, never, commitWarn, notify, hotkey, rHead, recent });
        FormClosing += (o, e) => { if (e.CloseReason == CloseReason.UserClosing) { SaveNever(); e.Cancel = true; Hide(); } };
        LoadSettings();
    }

    Label Label(string text, int x, int y, bool bold)
    {
        return new Label { Text = text, Left = x, Top = y, AutoSize = true, Font = bold ? new Font("Segoe UI", 9.5f, FontStyle.Bold) : Font };
    }

    void LoadSettings()
    {
        loading = true;
        var s = tray.Settings;
        foreach (var kv in boxes) kv.Value.Checked = (s.Areas & kv.Key) != 0;
        autoPct.Checked = s.AutoOnPercent; pct.Value = s.AutoPercent; autoEvery.Checked = s.AutoEvery; every.Value = s.EveryMinutes;
        notify.Checked = s.Notify; hotkey.Checked = s.Hotkey;
        gentle.Checked = s.Gentle; pause.Checked = s.PauseFullScreen; commitWarn.Checked = s.CommitWarn; always.Checked = s.AlwaysAdmin;
        never.Text = ProgramList.Join(s.Exclude);
        loading = false;
    }

    void SaveNever()
    {
        var names = ProgramList.Parse(never.Text);
        if (!names.SetEquals(tray.Settings.Exclude)) { tray.Settings.Exclude = names; tray.Settings.Save(); }
        never.Text = ProgramList.Join(names);
    }

    public void Refresh(Tray t)
    {
        var s = t.Last;
        ram.Text = string.Format("{0}% used", s.LoadPercent);
        ram.ForeColor = s.LoadPercent >= 90 ? Color.FromArgb(200, 40, 50) : s.LoadPercent >= 75 ? Color.FromArgb(190, 120, 0) : Color.FromArgb(30, 140, 70);
        ramBar.Value = s.LoadPercent;
        ramSub.Text = string.Format("Available {0:0.0} of {1:0.0} GB · cache {2:0.0} GB", s.AvailGb, s.TotalGb, s.CacheGb);
        var c = t.Commit;
        commit.Text = string.Format("Committed {0:0.0} of {1:0.0} GB", c.CommitGb, c.LimitGb) + (c.CanGrow ? string.Format(", can grow to {0:0} GB", c.CeilingGb) : string.Format(" ({0}%), cannot grow", c.Percent));
        commit.ForeColor = !t.Settings.CommitWarn || c.Percent < t.Settings.CommitYellow ? SystemColors.ControlText : c.Percent >= t.Settings.CommitRed ? Color.FromArgb(200, 40, 50) : Color.FromArgb(190, 120, 0);
        var set = t.Settings;
        kernel.Text = string.Format("{0:0.0} GB{1}", s.KernelGb, s.KernelGb >= set.KernelRedGb ? " — high" : s.KernelGb >= set.KernelYellowGb ? " — above normal" : " — normal");
        kernel.ForeColor = s.KernelGb >= set.KernelRedGb ? Color.FromArgb(200, 40, 50) : s.KernelGb >= set.KernelYellowGb ? Color.FromArgb(190, 120, 0) : Color.FromArgb(30, 140, 70);
        kernelSub.Text = string.Format("Paged {0:0.0} GB · nonpaged {1:0.0} GB. ", s.PagedGb, s.NonpagedGb)
            + (s.StuckFiles >= 0 ? string.Format("Likely leak: {0:0.0} GB ({1:0.00} M file objects). ", s.LeakGb, s.StuckFiles / 1e6) : "")
            + "Normally 1–2 GB in total.";
        graph.Data = t.History;
        graph.Invalidate();
        bool isAdmin = Native.IsAdmin();
        admin.Text = isAdmin ? "Running as administrator: every area can be cleaned."
            : t.Settings.AlwaysAdmin ? "\"Always run as administrator\" did not work at this start (see memcontrol.log). For all areas:"
            : "Without administrator rights only your own programs' working sets are cleaned. For all areas:";
        elevate.Visible = !isAdmin;
        always.Left = isAdmin ? elevate.Left : elevate.Left + 172; always.Width = isAdmin ? 348 : 176;
        always.Text = isAdmin ? "Always run as administrator (no prompt)" : "Always (no prompt)";
        if (always.Checked != t.Settings.AlwaysAdmin) { loading = true; always.Checked = t.Settings.AlwaysAdmin; loading = false; }
        autoStatus.Text = t.AutoStatus;
        if (!never.Focused) { string list = ProgramList.Join(t.Settings.Exclude); if (never.Text != list) never.Text = list; }
        clean.Enabled = !t.Cleaning;
        clean.Text = t.Cleaning ? "Cleaning…" : "Clean now";
        bool empty = t.Recent.Count == 0;
        if (recent.Items.Count == 0 || (empty ? recent.Items[0].Text != "Nothing cleaned yet." : recent.Items.Count != t.Recent.Count || recent.Items[0].Text != t.Recent[0]))
        {
            recent.BeginUpdate(); recent.Items.Clear();
            foreach (var line in t.Recent) recent.Items.Add(line);
            if (t.Recent.Count == 0) recent.Items.Add("Nothing cleaned yet.");
            recent.EndUpdate();
        }
        if (DateTime.Now - lastTop > TimeSpan.FromSeconds(6)) { lastTop = DateTime.Now; FillTop(); }
    }

    void FillTop()
    {
        var groups = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                long[] g;
                if (!groups.TryGetValue(p.ProcessName, out g)) groups[p.ProcessName] = g = new long[2];
                g[0]++; g[1] += p.PrivateMemorySize64;
            }
            catch { }
            finally { p.Dispose(); }
        }
        top.BeginUpdate(); top.Items.Clear();
        foreach (var kv in groups.OrderByDescending(k => k.Value[1]).Take(12))
            top.Items.Add(new ListViewItem(new[] { kv.Key, kv.Value[0].ToString(), (kv.Value[1] / (1 << 20)).ToString("N0") }));
        top.EndUpdate();
    }
}

class Bar : Control
{
    int value;
    public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
    public Bar() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Color.FromArgb(225, 228, 233))) using (var path = Tray.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 6)) g.FillPath(back, path);
        int w = (int)((Width - 1) * value / 100.0);
        if (w < 12) return;
        Color c = value >= 90 ? Color.FromArgb(220, 53, 69) : value >= 75 ? Color.FromArgb(230, 162, 30) : Color.FromArgb(40, 167, 90);
        using (var fill = new SolidBrush(c)) using (var path = Tray.Rounded(new Rectangle(0, 0, w, Height - 1), 6)) g.FillPath(fill, path);
    }
}

class Graph : Control
{
    public List<Snapshot> Data = new List<Snapshot>();
    public Graph() { DoubleBuffered = true; BackColor = Color.White; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var border = new Pen(Color.FromArgb(220, 224, 230))) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using (var grid = new Pen(Color.FromArgb(238, 240, 244)))
            for (int i = 1; i < 4; i++) g.DrawLine(grid, 1, Height * i / 4, Width - 2, Height * i / 4);
        if (Data.Count < 2) { TextRenderer.DrawText(g, "Collecting…", Font, ClientRectangle, Color.Gray); return; }
        float step = (Width - 2) / 359f;
        Draw(g, Color.FromArgb(40, 120, 220), s => s.LoadPercent / 100.0, step);
        double max = Math.Max(8, Data.Max(s => s.KernelGb) * 1.2);
        Draw(g, Color.FromArgb(200, 60, 140), s => s.KernelGb / max, step);
        TextRenderer.DrawText(g, "RAM %", new Font("Segoe UI", 8f), new Point(4, 2), Color.FromArgb(40, 120, 220));
        TextRenderer.DrawText(g, "kernel (up to " + max.ToString("0") + " GB)", new Font("Segoe UI", 8f), new Point(52, 2), Color.FromArgb(200, 60, 140));
    }
    void Draw(Graphics g, Color c, Func<Snapshot, double> y, float step)
    {
        var points = new List<PointF>();
        int start = 360 - Data.Count;
        for (int i = 0; i < Data.Count; i++) points.Add(new PointF(1 + (start + i) * step, (float)(Height - 2 - Math.Max(0, Math.Min(1, y(Data[i]))) * (Height - 4))));
        using (var pen = new Pen(c, 2f)) g.DrawLines(pen, points.ToArray());
    }
}
