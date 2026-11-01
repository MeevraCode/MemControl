// MemControl: a tray RAM cleaner in the spirit of Mem Reduct (henrypp/memreduct), plus what it does not have:
// a kernel-leak watch (some drivers leak kernel pool on every process start, e.g. file objects; no cleaner can
// free that, only a Restart), the top memory users and a 60-minute graph.
// Cleaning: process working sets, the system file cache, the modified page list, the standby list (low priority or
// all), page combining and the registry cache (NtSetSystemInformation, the same native calls Mem Reduct uses).
// Every area except the working sets of your own programs needs administrator rights.
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
    [STAThread]
    static int Main(string[] args)
    {
        bool show = args.Any(a => a.Equals("/show", StringComparison.OrdinalIgnoreCase));
        if (args.Any(a => a.Equals("/clean", StringComparison.OrdinalIgnoreCase)))
        {
            var r = Cleaner.Clean(Settings.Load().Areas);
            Log.Write("clean (/clean): " + r.Summary());
            return r.Errors.Count == 0 ? 0 : 1;
        }
        bool created;
        // MEMCONTROL_INSTANCE (tests only): a separate single-instance name, so a test never meets your running copy.
        string instance = Environment.GetEnvironmentVariable("MEMCONTROL_INSTANCE") ?? "";
        using (var once = new Mutex(true, "Local\\MemControl" + instance, out created))
        using (var ask = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\MemControlShow" + instance))
        {
            // "/takeover" (Run as administrator): the copy that started this one is closing; wait for it, then run.
            if (!created && args.Any(a => a.Equals("/takeover", StringComparison.OrdinalIgnoreCase)))
            {
                try { created = once.WaitOne(15000); }
                catch (AbandonedMutexException) { created = true; } // it exited while holding the mutex: ours now
                Log.Write("takeover: " + (created ? "the earlier copy closed, running now" : "the earlier copy did not close within 15 s"));
            }
            if (!created) { if (show) ask.Set(); return 0; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var tray = new Tray(ask, show);
            if (args.Any(a => a.Equals("/processes", StringComparison.OrdinalIgnoreCase))) tray.ShowProcesses(null);
            Application.Run(tray);
        }
        return 0;
    }

    public static string Folder { get { return AppDomain.CurrentDomain.BaseDirectory; } }
    public static string Exe { get { return Application.ExecutablePath; } }
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

    public static CleanResult Clean(Area areas)
    {
        lock (gate)
        {
            var r = new CleanResult { BeforeGb = Native.Read().AvailGb };
            bool admin = Native.IsAdmin();
            bool profile = admin && Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
            bool quota = admin && Native.EnablePrivilege("SeIncreaseQuotaPrivilege");

            if ((areas & Area.WorkingSets) != 0)
            {
                if (profile && MemoryList(MemoryEmptyWorkingSets)) r.Done.Add(Names[Area.WorkingSets]);
                else
                {
                    int trimmed = TrimOwnProcesses();
                    if (trimmed > 0) r.Done.Add(Names[Area.WorkingSets] + " (" + trimmed + " processes" + (admin ? "" : ", yours only") + ")");
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

    // Without admin rights: the working sets of the programs this user may open (PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA).
    static int TrimOwnProcesses()
    {
        int done = 0, self = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == 0 || p.Id == 4 || p.Id == self) continue;
                IntPtr h = Native.OpenProcess(0x1000 | 0x0100, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try { if (Native.EmptyWorkingSet(h)) done++; } finally { Native.CloseHandle(h); }
            }
            catch { }
            finally { p.Dispose(); }
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

    public Tray(EventWaitHandle ask, bool show)
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
        askTimer.Tick += (o, e) => { if (ask.WaitOne(0)) ShowWindow(); };
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
        SetIcon();
        icon.Text = Clip(string.Format("RAM {0}% · free {1:0.0} GB · kernel {2:0.0} GB", Last.LoadPercent, Last.AvailGb, Last.KernelGb));
        AutoClean();
        KernelWatch();
        if (window != null && !window.IsDisposed && window.Visible) window.Refresh(this);
    }

    void AutoClean()
    {
        if (cleaning || DateTime.Now - lastAuto < TimeSpan.FromMinutes(2)) return;
        if (Settings.AutoOnPercent && Last.LoadPercent >= Settings.AutoPercent) { lastAuto = DateTime.Now; CleanNow("auto: RAM " + Last.LoadPercent + "%"); }
        else if (Settings.AutoEvery && DateTime.Now - lastAuto >= TimeSpan.FromMinutes(Settings.EveryMinutes)
            && (lastAuto != DateTime.MinValue || DateTime.Now - First.At >= TimeSpan.FromMinutes(Settings.EveryMinutes)))
        { lastAuto = DateTime.Now; CleanNow("auto every " + Settings.EveryMinutes + " min"); }
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

    public void CleanNow(string why)
    {
        if (cleaning) return;
        cleaning = true;
        var areas = Settings.Areas;
        var worker = new BackgroundWorker();
        CleanResult result = null;
        worker.DoWork += (o, e) => result = Cleaner.Clean(areas);
        worker.RunWorkerCompleted += (o, e) =>
        {
            cleaning = false;
            string line = DateTime.Now.ToString("HH:mm") + " " + why + ": " + (result != null ? result.Summary() : "failed");
            Recent.Insert(0, line);
            if (Recent.Count > 20) Recent.RemoveAt(Recent.Count - 1);
            Log.Write(why + ": " + (result != null ? result.Summary() : "failed"));
            if (Settings.Notify && result != null)
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
        try
        {
            Process.Start(new ProcessStartInfo(Program.Exe, "/show /takeover") { UseShellExecute = true, Verb = "runas", WorkingDirectory = Program.Folder });
            Quit();
        }
        catch (Win32Exception) { /* the UAC window was declined: keep running as is */ }
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
        Color color = Last.KernelGb >= Settings.KernelRedGb || pct >= 90 ? Color.FromArgb(220, 53, 69)
            : Last.KernelGb >= Settings.KernelYellowGb || pct >= 75 ? Color.FromArgb(230, 162, 30) : Color.FromArgb(40, 167, 90);
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
    readonly Label ram = new Label(), ramSub = new Label(), kernel = new Label(), kernelSub = new Label(), admin = new Label();
    readonly Bar ramBar = new Bar();
    readonly Graph graph = new Graph();
    readonly ListView top = new ListView(), recent = new ListView();
    readonly Button clean = new Button(), elevate = new Button();
    readonly Dictionary<Area, CheckBox> boxes = new Dictionary<Area, CheckBox>();
    readonly CheckBox autoPct = new CheckBox(), autoEvery = new CheckBox(), notify = new CheckBox(), hotkey = new CheckBox();
    readonly NumericUpDown pct = new NumericUpDown(), every = new NumericUpDown();
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
        ClientSize = new Size(760, 600);
        BackColor = Color.FromArgb(248, 249, 251);

        // Left column: RAM, kernel, graph, top programs.
        var head = Label("Memory (RAM)", 16, 12, true);
        ram.SetBounds(16, 34, 360, 40); ram.Font = new Font("Segoe UI", 22f, FontStyle.Bold);
        ramBar.SetBounds(16, 78, 360, 14);
        ramSub.SetBounds(16, 96, 360, 40);
        var kHead = Label("Windows kernel", 16, 142, true);
        kernel.SetBounds(16, 162, 360, 24); kernel.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        kernelSub.SetBounds(16, 188, 360, 74); kernelSub.ForeColor = Color.DimGray;
        var gHead = Label("Last 60 minutes", 16, 266, true);
        graph.SetBounds(16, 286, 360, 90);
        var tHead = Label("Top memory users", 16, 382, true);
        top.SetBounds(16, 402, 360, 184); top.View = View.Details; top.FullRowSelect = true; top.HeaderStyle = ColumnHeaderStyle.Nonclickable;
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
        elevate.SetBounds(x, 98, 240, 28); elevate.Text = "Run as administrator";
        elevate.Click += (o, e) => tray.RestartAsAdmin();
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
        var autoHead = Label("Automatic", x, y, true); y += 20;
        autoPct.SetBounds(x, y, 210, 24); autoPct.Text = "Clean when RAM use ≥"; pct.SetBounds(x + 214, y, 52, 24); pct.Minimum = 50; pct.Maximum = 99;
        var pctUnit = Label("%", x + 270, y + 3, false); y += 28;
        autoEvery.SetBounds(x, y, 210, 24); autoEvery.Text = "Clean every"; every.SetBounds(x + 214, y, 52, 24); every.Minimum = 5; every.Maximum = 720;
        var everyUnit = Label("min", x + 270, y + 3, false); y += 28;
        notify.SetBounds(x, y, 348, 22); notify.Text = "Notify how much was freed"; y += 22;
        hotkey.SetBounds(x, y, 348, 22); hotkey.Text = "Clean with Ctrl+Shift+F1"; y += 28;
        EventHandler save = (o, e) =>
        {
            if (loading) return;
            var s = tray.Settings;
            s.AutoOnPercent = autoPct.Checked; s.AutoPercent = (int)pct.Value; s.AutoEvery = autoEvery.Checked; s.EveryMinutes = (int)every.Value;
            s.Notify = notify.Checked; bool hk = s.Hotkey != hotkey.Checked; s.Hotkey = hotkey.Checked; s.Save();
            if (hk) tray.ApplyHotkey();
        };
        autoPct.CheckedChanged += save; pct.ValueChanged += save; autoEvery.CheckedChanged += save; every.ValueChanged += save; notify.CheckedChanged += save; hotkey.CheckedChanged += save;
        var rHead = Label("Recent cleanings", x, y, true); y += 20;
        recent.SetBounds(x, y, 348, 586 - y); recent.View = View.Details; recent.HeaderStyle = ColumnHeaderStyle.None; recent.ShowItemToolTips = true; recent.Columns.Add("", 330);
        var restart = new LinkLabel { Text = "Restart computer…", Left = 16, Top = 0, AutoSize = true };
        restart.LinkClicked += (o, e) => tray.AskRestart();
        kernelSub.Controls.Add(restart); restart.Top = 54;
        var explain = new LinkLabel { Text = "What is this? Why does it grow?", AutoSize = true, Left = 150, Top = 54 };
        explain.LinkClicked += (o, e) => KernelHelp.Show(this, tray.Last);
        kernelSub.Controls.Add(explain);

        Controls.AddRange(new Control[] { head, ram, ramBar, ramSub, kHead, kernel, kernelSub, gHead, graph, tHead, top, clean, admin, elevate, aHead, tip, autoHead, autoPct, pct, pctUnit, autoEvery, every, everyUnit, notify, hotkey, rHead, recent });
        FormClosing += (o, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
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
        loading = false;
    }

    public void Refresh(Tray t)
    {
        var s = t.Last;
        ram.Text = string.Format("{0}% used", s.LoadPercent);
        ram.ForeColor = s.LoadPercent >= 90 ? Color.FromArgb(200, 40, 50) : s.LoadPercent >= 75 ? Color.FromArgb(190, 120, 0) : Color.FromArgb(30, 140, 70);
        ramBar.Value = s.LoadPercent;
        ramSub.Text = string.Format("Available {0:0.0} of {1:0.0} GB · cache {2:0.0} GB\nCommitted {3:0.0} of {4:0.0} GB (with the page file)", s.AvailGb, s.TotalGb, s.CacheGb, s.CommitGb, s.CommitLimitGb);
        var set = t.Settings;
        kernel.Text = string.Format("{0:0.0} GB{1}", s.KernelGb, s.KernelGb >= set.KernelRedGb ? " — high" : s.KernelGb >= set.KernelYellowGb ? " — above normal" : " — normal");
        kernel.ForeColor = s.KernelGb >= set.KernelRedGb ? Color.FromArgb(200, 40, 50) : s.KernelGb >= set.KernelYellowGb ? Color.FromArgb(190, 120, 0) : Color.FromArgb(30, 140, 70);
        kernelSub.Text = string.Format("Paged {0:0.0} GB · nonpaged {1:0.0} GB. ", s.PagedGb, s.NonpagedGb)
            + (s.StuckFiles >= 0 ? string.Format("Likely leak: {0:0.0} GB ({1:0.00} M file objects). ", s.LeakGb, s.StuckFiles / 1e6) : "")
            + "Normally 1–2 GB in total.";
        graph.Data = t.History;
        graph.Invalidate();
        bool isAdmin = Native.IsAdmin();
        admin.Text = isAdmin ? "Running as administrator: every area can be cleaned." : "Without administrator rights only your own programs' working sets are cleaned. For all areas:";
        elevate.Visible = !isAdmin;
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
