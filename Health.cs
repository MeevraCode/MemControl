// "Why is it slow?": what besides RAM can hold this PC back. One line for each cause, with a color, the figure and
// what to do. Everything is read without administrator rights; a figure this PC does not report is shown as such.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>Windows performance counters by their English names (PDH), so they work in every display language.</summary>
class Counters : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQueryW(string source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterArrayW(IntPtr counter, int format, ref int size, out int count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);

    IntPtr query;
    readonly Dictionary<string, IntPtr> counters = new Dictionary<string, IntPtr>();

    /// <summary>How many readings were taken. Rates (per second, percent) are valid from the second one.</summary>
    public int Collections;

    public Counters(params string[] paths)
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
            foreach (var path in paths)
            {
                IntPtr counter;
                if (PdhAddEnglishCounterW(query, path, IntPtr.Zero, out counter) == 0) counters[path] = counter;
            }
        }
        catch { query = IntPtr.Zero; }
    }

    public void Collect()
    {
        if (query == IntPtr.Zero) return;
        PdhCollectQueryData(query);
        Collections++;
    }

    /// <summary>Every instance of a counter with its value; empty when this PC does not have the counter.</summary>
    public List<KeyValuePair<string, double>> Many(string path)
    {
        var values = new List<KeyValuePair<string, double>>();
        IntPtr counter;
        if (query == IntPtr.Zero || !counters.TryGetValue(path, out counter)) return values;
        int size = 0, count;
        if (PdhGetFormattedCounterArrayW(counter, 0x200, ref size, out count, IntPtr.Zero) != unchecked((int)0x800007D2) || size <= 0) return values; // PDH_FMT_DOUBLE, PDH_MORE_DATA
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, 0x200, ref size, out count, buffer) != 0) return values;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buffer + i * 24; // { name pointer, status, double }
                int status = Marshal.ReadInt32(item, 8);
                if (status != 0 && status != 1) continue;
                values.Add(new KeyValuePair<string, double>(Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "", BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16))));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return values;
    }

    /// <summary>The value of a counter with one instance, or NaN.</summary>
    public double One(string path)
    {
        var values = Many(path);
        return values.Count > 0 ? values[0].Value : double.NaN;
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero) PdhCloseQuery(query);
        query = IntPtr.Zero;
    }
}

class GpuAdapter
{
    public string Luid = "";                  // as performance counters name it: luid_0x00000000_0x0001A2B3
    public double TemperatureC = double.NaN;  // NaN when the driver does not report it
    public long DedicatedBytes;               // its own memory; 0 when unknown
}

/// <summary>Graphics adapters with their temperature and memory size, the way Task Manager reads them (D3DKMT).</summary>
static class Gpu
{
    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(IntPtr data);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(IntPtr data);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref int adapter);

    public static List<GpuAdapter> Adapters()
    {
        var list = new List<GpuAdapter>();
        IntPtr en = Marshal.AllocHGlobal(16), array = IntPtr.Zero;
        try
        {
            Marshal.WriteInt64(en, 0, 0);
            Marshal.WriteInt64(en, 8, 0);
            if (D3DKMTEnumAdapters2(en) != 0) return list;
            int n = Marshal.ReadInt32(en);
            if (n <= 0 || n > 64) return list;
            array = Marshal.AllocHGlobal(n * 20); // { handle, LUID low, LUID high, sources, flag }
            Marshal.WriteInt32(en, 0, n);
            Marshal.WriteIntPtr(en, 8, array);
            if (D3DKMTEnumAdapters2(en) != 0) return list;
            n = Marshal.ReadInt32(en);
            for (int i = 0; i < n; i++)
            {
                int handle = Marshal.ReadInt32(array, i * 20);
                var a = new GpuAdapter { Luid = string.Format("luid_0x{0:X8}_0x{1:X8}", Marshal.ReadInt32(array, i * 20 + 8), Marshal.ReadInt32(array, i * 20 + 4)) };
                byte[] perf = Query(handle, 62, 64), size = Query(handle, 3, 24); // adapter performance data; segment sizes
                if (perf != null) { uint tenths = BitConverter.ToUInt32(perf, 56); if (tenths > 0 && tenths < 2000) a.TemperatureC = tenths / 10.0; }
                if (size != null) a.DedicatedBytes = BitConverter.ToInt64(size, 0);
                if (!list.Any(x => x.Luid == a.Luid)) list.Add(a);
                D3DKMTCloseAdapter(ref handle);
            }
        }
        catch { }
        finally
        {
            if (array != IntPtr.Zero) Marshal.FreeHGlobal(array);
            Marshal.FreeHGlobal(en);
        }
        return list;
    }

    static byte[] Query(int adapter, int type, int size)
    {
        IntPtr data = Marshal.AllocHGlobal(size), query = Marshal.AllocHGlobal(24);
        try
        {
            for (int i = 0; i < size; i += 4) Marshal.WriteInt32(data, i, 0);
            Marshal.WriteInt32(query, 0, adapter);
            Marshal.WriteInt32(query, 4, type);
            Marshal.WriteIntPtr(query, 8, data);
            Marshal.WriteInt32(query, 16, size);
            Marshal.WriteInt32(query, 20, 0);
            if (D3DKMTQueryAdapterInfo(query) != 0) return null;
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, size);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(query); Marshal.FreeHGlobal(data); }
    }
}

/// <summary>Mains or battery, battery saver, the power plan, the power mode, and the cap on processor speed.</summary>
class PowerInfo
{
    [StructLayout(LayoutKind.Sequential)]
    struct Status { public byte AcLine, BatteryFlag, BatteryPercent, Saver; public int LifeTime, FullLifeTime; }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out Status status);
    [DllImport("powrprof.dll")] static extern int PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern int PowerReadFriendlyName(IntPtr root, IntPtr scheme, IntPtr sub, IntPtr setting, byte[] buffer, ref int size);
    [DllImport("powrprof.dll")] static extern int PowerReadACValueIndex(IntPtr root, IntPtr scheme, ref Guid sub, ref Guid setting, out int value);
    [DllImport("powrprof.dll")] static extern int PowerReadDCValueIndex(IntPtr root, IntPtr scheme, ref Guid sub, ref Guid setting, out int value);
    [DllImport("powrprof.dll")] static extern int PowerGetEffectiveOverlayScheme(out Guid overlay);

    public bool HasBattery, OnBattery, Saver;
    public int BatteryPercent = -1, MaxProcessorState = 100;
    public string Plan = "", Mode = "";

    public static PowerInfo Read()
    {
        var p = new PowerInfo();
        try
        {
            Status s;
            if (GetSystemPowerStatus(out s))
            {
                p.HasBattery = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;
                p.OnBattery = s.AcLine == 0;
                p.Saver = s.Saver == 1;
                if (s.BatteryPercent <= 100) p.BatteryPercent = s.BatteryPercent;
            }
            IntPtr scheme;
            if (PowerGetActiveScheme(IntPtr.Zero, out scheme) == 0)
            {
                var buffer = new byte[1024];
                int size = buffer.Length, value;
                if (PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0 && size >= 2) p.Plan = Encoding.Unicode.GetString(buffer, 0, size - 2);
                Guid sub = new Guid("54533251-82be-4824-96c1-47b60b740d00"), max = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"); // processor settings, maximum processor state
                int rc = p.OnBattery ? PowerReadDCValueIndex(IntPtr.Zero, scheme, ref sub, ref max, out value) : PowerReadACValueIndex(IntPtr.Zero, scheme, ref sub, ref max, out value);
                if (rc == 0 && value > 0 && value <= 100) p.MaxProcessorState = value;
                Marshal.FreeHGlobal(scheme);
            }
            Guid overlay;
            if (PowerGetEffectiveOverlayScheme(out overlay) == 0)
                p.Mode = overlay == new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a") ? "best power efficiency"
                    : overlay == new Guid("ded574b5-45a0-4f42-8737-46345c09c238") ? "best performance"
                    : overlay == Guid.Empty ? "balanced" : "";
        }
        catch { }
        return p;
    }
}

class HealthRow
{
    public int Level;       // 0 fine, 1 worth a look, 2 slowing the PC now, -1 not measured
    public string Title = "", Value = "", Advice = "";
    public Action Action;   // what a click on the line does, or null
}

class Health
{
    const double Gb = 1073741824.0;
    const string CpuUtility = @"\Processor Information(_Total)\% Processor Utility", CpuTime = @"\Processor Information(_Total)\% Processor Time",
        CpuSpeed = @"\Processor Information(_Total)\% Processor Performance", CpuLimit = @"\Processor Information(_Total)\% Performance Limit",
        Dpc = @"\Processor Information(_Total)\% DPC Time", Interrupt = @"\Processor Information(_Total)\% Interrupt Time",
        Temperature = @"\Thermal Zone Information(*)\Temperature", PassiveLimit = @"\Thermal Zone Information(*)\% Passive Limit",
        DiskIdle = @"\PhysicalDisk(_Total)\% Idle Time", DiskBytes = @"\PhysicalDisk(_Total)\Disk Bytes/sec",
        PagesIn = @"\Memory\Pages Input/sec", StandbyNormal = @"\Memory\Standby Cache Normal Priority Bytes", StandbyReserve = @"\Memory\Standby Cache Reserve Bytes",
        StandbyCore = @"\Memory\Standby Cache Core Bytes", FreeZero = @"\Memory\Free & Zero Page List Bytes",
        GpuDedicated = @"\GPU Adapter Memory(*)\Dedicated Usage", GpuEngine = @"\GPU Engine(*)\Utilization Percentage", GpuProcess = @"\GPU Process Memory(*)\Dedicated Usage";

    public static readonly string[] Paths = { CpuUtility, CpuTime, CpuSpeed, CpuLimit, Dpc, Interrupt, Temperature, PassiveLimit, DiskIdle, DiskBytes,
        PagesIn, StandbyNormal, StandbyReserve, StandbyCore, FreeZero, GpuDedicated, GpuEngine, GpuProcess };

    readonly Queue<double> pages = new Queue<double>(); // the last readings of pages read back from disk, for an average
    List<string> startup = new List<string>();
    List<string> drives = new List<string>();
    int driveLevel;
    DateTime startupAt = DateTime.MinValue, drivesAt = DateTime.MinValue;

    static HealthRow Row(int level, string title, string value, string advice, Action action = null)
    {
        return new HealthRow { Level = level, Title = title, Value = value, Advice = advice, Action = action };
    }

    static void Open(string target)
    {
        try { Process.Start("explorer.exe", target); } catch { } // through the shell, so nothing opens with administrator rights
    }

    /// <summary>The names of the programs Windows starts at sign-in (the Run keys and the Startup folders).</summary>
    public static List<string> StartupNames()
    {
        var names = new List<string>();
        try
        {
            const string run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            using (var k = Registry.CurrentUser.OpenSubKey(run)) if (k != null) names.AddRange(k.GetValueNames());
            using (var k = Registry.LocalMachine.OpenSubKey(run)) if (k != null) names.AddRange(k.GetValueNames());
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")) if (k != null) names.AddRange(k.GetValueNames());
            foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
            {
                string dir = Environment.GetFolderPath(folder);
                if (Directory.Exists(dir)) names.AddRange(Directory.GetFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileNameWithoutExtension));
            }
        }
        catch { }
        return names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string Adapter(string instance)
    {
        int at = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase), end = instance.IndexOf("_phys", StringComparison.OrdinalIgnoreCase);
        return at >= 0 && end > at ? instance.Substring(at, end - at) : instance;
    }

    static int PidOf(string instance)
    {
        var parts = instance.Split('_');
        int pid;
        return parts.Length > 1 && parts[0] == "pid" && int.TryParse(parts[1], out pid) ? pid : -1;
    }

    /// <summary>One line for each thing that can slow the PC, from what Windows reports now.</summary>
    public List<HealthRow> Collect(Tray t, Counters c)
    {
        var rows = new List<HealthRow>();
        var set = t.Settings;
        var s = t.Last;
        var procs = t.Processes;
        bool measured = c.Collections >= 2;
        Action processes = () => t.ShowProcesses(null);
        Func<int, string> nameOf = pid => { var p = procs.FirstOrDefault(x => x.Pid == pid); return p != null && p.Name != "" ? p.Name : "process " + pid; };
        var power = PowerInfo.Read();

        // Processor: how busy it is and who keeps it busy.
        double busy = c.One(CpuUtility);
        if (double.IsNaN(busy)) busy = c.One(CpuTime);
        busy = Math.Min(100, busy);
        string busiest = Sys.Top(procs, p => p.CpuPercent, 3, v => v.ToString("0") + "%", 1);
        if (!measured || double.IsNaN(busy)) rows.Add(Row(-1, "Processor", "measuring…", "The first figures take a few seconds.", processes));
        else rows.Add(Row(busy >= 90 ? 2 : busy >= 75 ? 1 : 0, "Processor", busy.ToString("0") + "% busy",
            (busy >= 75 ? "The processor is what everything waits for right now. " : "") + (busiest != "" ? "Busiest: " + busiest + "." : "No program is using it much."), processes));

        // Processor speed: is something holding it under its rated speed?
        double speed = c.One(CpuSpeed), limit = c.One(CpuLimit);
        var passive = c.Many(PassiveLimit);
        double cooling = passive.Count > 0 ? passive.Min(v => v.Value) : 100;
        if (measured && !double.IsNaN(speed))
        {
            bool limited = !double.IsNaN(limit) && limit < 99, slow = speed < 70 && busy >= 50;
            string why = cooling < 100 ? "Windows is slowing the processor to " + cooling.ToString("0") + "% to cool it. Clear the vents, lift the back of the laptop, check the fan."
                : limited ? "The firmware holds the processor at " + limit.ToString("0") + "% (heat, or a weak power supply)."
                : power.MaxProcessorState < 100 ? "The power plan caps the processor at " + power.MaxProcessorState + "%."
                : slow ? "It runs well under its rated speed while busy: the power mode, the charger or heat is holding it back."
                : "Nothing is holding it back.";
            rows.Add(Row(cooling < 100 ? 2 : limited || slow || power.MaxProcessorState < 100 ? 1 : 0, "Processor speed", speed.ToString("0") + "% of its rated speed", why));
        }

        // Temperatures, where this PC reports them.
        var zones = c.Many(Temperature);
        var gpus = Gpu.Adapters();
        double cpuTemp = zones.Count > 0 ? zones.Max(v => v.Value) - 273.15 : double.NaN;
        var gpuTemps = gpus.Where(g => !double.IsNaN(g.TemperatureC)).Select(g => g.TemperatureC).ToList();
        double gpuTemp = gpuTemps.Count > 0 ? gpuTemps.Max() : double.NaN;
        if (double.IsNaN(cpuTemp) && double.IsNaN(gpuTemp)) rows.Add(Row(-1, "Temperature", "not reported", "This PC does not report its temperatures to Windows."));
        else
        {
            bool hot = cpuTemp >= 95 || gpuTemp >= 90;
            string value = (double.IsNaN(cpuTemp) ? "" : "system " + cpuTemp.ToString("0") + " °C") + (double.IsNaN(cpuTemp) || double.IsNaN(gpuTemp) ? "" : " · ") + (double.IsNaN(gpuTemp) ? "" : "graphics " + gpuTemp.ToString("0") + " °C");
            rows.Add(Row(hot ? 1 : 0, "Temperature", value, hot ? "It is running hot. Hot chips slow themselves down: clear the vents and give the PC air." : "Not hot enough to slow anything down."));
        }

        // Graphics: how busy, and how full its own memory is.
        var adapterMemory = c.Many(GpuDedicated);
        var engines = c.Many(GpuEngine);
        if (adapterMemory.Count == 0 && engines.Count == 0) rows.Add(Row(-1, "Graphics", "not reported", "Windows gives no graphics figures on this PC."));
        else
        {
            var fullest = adapterMemory.OrderByDescending(v => v.Value).FirstOrDefault();
            var adapter = fullest.Key == null ? null : gpus.FirstOrDefault(g => fullest.Key.StartsWith(g.Luid, StringComparison.OrdinalIgnoreCase));
            double usedGb = fullest.Value / Gb, totalGb = adapter != null ? adapter.DedicatedBytes / Gb : 0;
            double gpuBusy = engines.Where(v => v.Key.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)).GroupBy(v => Adapter(v.Key)).Select(g => g.Sum(v => v.Value)).DefaultIfEmpty(0).Max();
            string users = string.Join(", ", c.Many(GpuProcess).GroupBy(v => PidOf(v.Key)).Select(g => new { Pid = g.Key, Bytes = g.Sum(v => v.Value) }).Where(g => g.Bytes >= 100 * 1048576.0)
                .OrderByDescending(g => g.Bytes).Take(3).Select(g => nameOf(g.Pid) + " " + (g.Bytes / Gb).ToString("0.0") + " GB"));
            bool full = totalGb > 0.5 && usedGb / totalGb >= 0.92;
            rows.Add(Row(full ? 1 : 0, "Graphics", (measured ? Math.Min(100, gpuBusy).ToString("0") + "% busy · " : "") + "memory " + usedGb.ToString("0.0") + (totalGb > 0.5 ? " of " + totalGb.ToString("0.0") : "") + " GB",
                (full ? "Graphics memory is almost full. Games stutter when it runs out: lower the texture quality or close what uses it. " : "") + (users != "" ? "Largest: " + users + "." : "Nothing is using much graphics memory."), processes));
        }

        // Disk: how busy, and who reads and writes the most.
        double idle = c.One(DiskIdle), diskBytes = c.One(DiskBytes);
        if (measured && !double.IsNaN(idle))
        {
            double active = 100 - Math.Max(0, Math.Min(100, idle));
            string writers = Sys.Top(procs, p => p.IoBytesPerSecond / 1048576.0, 3, v => v.ToString("0.0") + " MB/s", 0.5);
            rows.Add(Row(active >= 95 ? 2 : active >= 80 ? 1 : 0, "Disk", active.ToString("0") + "% busy" + (double.IsNaN(diskBytes) ? "" : " · " + (diskBytes / 1048576.0).ToString("0.0") + " MB/s"),
                (active >= 80 ? "The disk is working at its limit, so everything that opens or saves files waits. " : "") + (writers != "" ? "Most active: " + writers + "." : "No program is reading or writing much."), processes));
        }

        // Disk space.
        if (DateTime.Now - drivesAt > TimeSpan.FromSeconds(15))
        {
            drivesAt = DateTime.Now;
            drives = new List<string>();
            driveLevel = 0;
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                    double freeGb = d.AvailableFreeSpace / Gb, percent = 100.0 * d.AvailableFreeSpace / d.TotalSize;
                    int level = percent < 5 || freeGb < 5 ? 2 : percent < 10 || freeGb < 15 ? 1 : 0;
                    driveLevel = Math.Max(driveLevel, level);
                    drives.Add(d.Name.TrimEnd('\\') + " " + freeGb.ToString("0") + " GB free" + (level > 0 ? " (" + percent.ToString("0") + "%)" : ""));
                }
            }
            catch { }
        }
        if (drives.Count > 0)
            rows.Add(Row(driveLevel, "Disk space", string.Join(" · ", drives), driveLevel == 2 ? "A drive is almost full. Windows slows down and updates fail when the system drive has no room: free some space."
                : driveLevel == 1 ? "A drive is getting full. Leave at least a tenth of it free." : "Enough free space."));

        // Memory: is Windows reading memory back from the disk?
        double pagesIn = c.One(PagesIn);
        if (measured && !double.IsNaN(pagesIn))
        {
            pages.Enqueue(pagesIn);
            while (pages.Count > 5) pages.Dequeue();
            double paging = pages.Average();
            double standby = c.One(StandbyNormal) + c.One(StandbyReserve) + c.One(StandbyCore), free = c.One(FreeZero);
            long compressed = procs.Where(p => p.Name == "Memory Compression").Select(p => p.WorkingSet).FirstOrDefault();
            string value = "RAM " + s.LoadPercent + "%" + (double.IsNaN(free) ? "" : " · free " + (free / Gb).ToString("0.0") + " GB") + (double.IsNaN(standby) ? "" : " · cache " + (standby / Gb).ToString("0.0") + " GB")
                + (compressed > 0 ? " · compressed " + (compressed / Gb).ToString("0.0") + " GB" : "");
            rows.Add(Row(paging >= 2000 ? 2 : paging >= 600 ? 1 : 0, "Memory", value, paging >= 600
                ? "Windows keeps reading memory back from the disk (" + paging.ToString("N0") + " pages a second): there is too little RAM for what is running. Close something large: " + t.Largest + "."
                : "Windows is not short of RAM right now. The cache is given back the moment a program needs it.", processes));
        }

        // Commit: memory that programs have reserved.
        var commit = t.Commit;
        int commitLevel = commit.Percent >= set.CommitRed ? 2 : commit.Percent >= set.CommitYellow ? 1 : 0;
        rows.Add(Row(commitLevel, "Reserved memory (commit)", commit.CommitGb.ToString("0.0") + " of " + commit.LimitGb.ToString("0.0") + " GB" + (commit.CanGrow ? ", can grow to " + commit.CeilingGb.ToString("0") : ""),
            commitLevel > 0 ? "Programs have reserved almost all the memory Windows can promise. Close a large program, or programs will fail for lack of memory."
                : "There is room. Programs fail only when this reaches a limit that cannot rise.", () => CommitHelp.Show(null, t)));

        // Kernel memory: the driver-leak watch.
        int kernelLevel = s.KernelGb >= set.KernelRedGb ? 2 : s.KernelGb >= set.KernelYellowGb ? 1 : 0;
        rows.Add(Row(kernelLevel, "Windows kernel memory", s.KernelGb.ToString("0.0") + " GB",
            (kernelLevel > 0 ? "A driver is leaking kernel memory. Cleaning cannot free it: Restart (not Shut down) when convenient. " : "Normal. ") + t.KernelForecast, () => t.ShowLeaks()));

        // Power.
        string source = !power.HasBattery ? "mains" : power.OnBattery ? "battery" + (power.BatteryPercent >= 0 ? " " + power.BatteryPercent + "%" : "") : "plugged in";
        bool saving = power.Saver || power.Mode == "best power efficiency";
        rows.Add(Row(saving ? 1 : 0, "Power", source + (power.Plan != "" ? " · plan \"" + power.Plan + "\"" : "") + (power.Mode != "" ? " · " + power.Mode : ""),
            power.Saver ? "Battery saver is on: Windows slows the PC down to save the battery."
                : power.Mode == "best power efficiency" ? "The power mode is set to save energy, which slows the PC. Change it in Settings, Power."
                : "The power settings are not slowing the PC. A laptop maker's own quiet mode can still do so.", () => Open("ms-settings:powersleep")));

        // Drivers: time the processor spends in interrupts and deferred driver work.
        double dpc = c.One(Dpc), interrupts = c.One(Interrupt);
        if (measured && !double.IsNaN(dpc) && !double.IsNaN(interrupts))
        {
            double drivers = dpc + interrupts;
            rows.Add(Row(drivers >= 10 ? 2 : drivers >= 4 ? 1 : 0, "Drivers", drivers.ToString("0.0") + "% of the processor",
                drivers >= 4 ? "Drivers are taking a lot of processor time. Sound crackles and the pointer stutters when this is high: update the network, sound and graphics drivers."
                    : "Drivers are not getting in the way."));
        }

        // How long this Windows session has been running.
        DateTime boot = t.Boot;
        if (boot != DateTime.MinValue)
        {
            double days = (DateTime.Now - boot).TotalDays;
            bool fastStartup = Equals(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0), 1);
            bool pending = false;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) pending = k != null;
            }
            catch { }
            rows.Add(Row(pending || days >= 14 ? 1 : 0, "Windows session", "running for " + (days >= 1 ? days.ToString("0.0") + " days" : (days * 24).ToString("0") + " hours"),
                pending ? "Windows is waiting for a restart to finish installing updates."
                    : (days >= 14 ? "A long session collects leftovers. Restart when convenient. " : "") + (fastStartup ? "Shut down does not end this session (Fast Startup is on); Restart does." : "Shut down and Restart both end the session."),
                () => t.AskRestart()));
        }

        // Programs that start with Windows.
        if (DateTime.Now - startupAt > TimeSpan.FromSeconds(30)) { startupAt = DateTime.Now; startup = StartupNames(); }
        rows.Add(Row(startup.Count >= 12 ? 1 : 0, "Startup programs", startup.Count.ToString(),
            (startup.Count >= 12 ? "Many programs start with Windows and keep running. Switch off the ones you do not need: " : "They start with Windows: ") + string.Join(", ", startup.Take(10)) + (startup.Count > 10 ? ", …" : "") + ".",
            () => Open("ms-settings:startupapps")));

        // Programs that have stopped answering.
        try
        {
            var hung = Sys.HungPids().Select(nameOf).Distinct().ToList();
            if (hung.Count > 0) rows.Add(Row(1, "Not responding", string.Join(", ", hung.Take(4)), "These programs have stopped answering. Wait a little, or end them in the process manager.", processes));
        }
        catch { }

        return rows;
    }

    /// <summary>The one sentence above the list: the worst line, or that nothing is wrong.</summary>
    public static string Verdict(List<HealthRow> rows)
    {
        var worst = rows.Where(r => r.Level > 0).OrderByDescending(r => r.Level).ToList();
        if (worst.Count == 0) return rows.Any(r => r.Level < 0 && r.Value.StartsWith("measuring")) ? "Measuring…" : "Nothing here is slowing this PC right now.";
        return (worst[0].Level == 2 ? "Slowing this PC now: " : "Worth a look: ") + string.Join(", ", worst.Take(3).Select(r => r.Title.ToLowerInvariant())) + ".";
    }

    public static string Report(List<HealthRow> rows)
    {
        var text = new StringBuilder("MemControl " + Program.Version + " — why is it slow? " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n");
        foreach (var r in rows) text.Append((r.Level == 2 ? "[!!] " : r.Level == 1 ? "[!]  " : r.Level == 0 ? "[ok] " : "[--] ") + r.Title + ": " + r.Value + "\r\n     " + r.Advice + "\r\n");
        return text.ToString();
    }
}

/// <summary>The lines of the "Why is it slow?" window: a colored dot, the title, the figure, and the advice beneath.</summary>
class HealthList : Control
{
    public const int RowHeight = 48;
    List<HealthRow> rows = new List<HealthRow>();
    int hover = -1;
    readonly ToolTip tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 500 };
    readonly Font title = new Font("Segoe UI", 10f, FontStyle.Bold), advice = new Font("Segoe UI", 8.75f);

    public HealthList() { DoubleBuffered = true; BackColor = Color.White; }

    public void SetRows(List<HealthRow> list)
    {
        rows = list;
        Height = Math.Max(RowHeight, rows.Count * RowHeight);
        Invalidate();
    }

    public static Color ColorOf(int level)
    {
        return level == 2 ? Color.FromArgb(220, 53, 69) : level == 1 ? Color.FromArgb(230, 162, 30) : level == 0 ? Color.FromArgb(40, 167, 90) : Color.FromArgb(172, 176, 184);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int y = i * RowHeight;
            if (i == hover && r.Action != null) using (var back = new SolidBrush(Color.FromArgb(243, 246, 251))) g.FillRectangle(back, 0, y, Width, RowHeight - 1);
            using (var dot = new SolidBrush(ColorOf(r.Level))) g.FillEllipse(dot, 14, y + 9, 13, 13);
            Color figure = r.Level == 2 ? Color.FromArgb(200, 40, 50) : r.Level == 1 ? Color.FromArgb(190, 120, 0) : Color.FromArgb(40, 44, 52);
            TextRenderer.DrawText(g, r.Title, title, new Rectangle(36, y + 5, 210, 20), Color.FromArgb(40, 44, 52), flags | TextFormatFlags.Left);
            TextRenderer.DrawText(g, r.Value, title, new Rectangle(246, y + 5, Width - 260, 20), figure, flags | TextFormatFlags.Right);
            TextRenderer.DrawText(g, r.Advice, advice, new Rectangle(36, y + 26, Width - 50, 18), Color.FromArgb(105, 110, 118), flags | TextFormatFlags.Left);
            using (var line = new Pen(Color.FromArgb(236, 238, 242))) g.DrawLine(line, 12, y + RowHeight - 1, Width - 12, y + RowHeight - 1);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = e.Y / RowHeight;
        if (i < 0 || i >= rows.Count) i = -1;
        if (i == hover) return;
        hover = i;
        Cursor = i >= 0 && rows[i].Action != null ? Cursors.Hand : Cursors.Default;
        tip.SetToolTip(this, i >= 0 ? rows[i].Advice : "");
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Cursor = Cursors.Default; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int i = e.Y / RowHeight;
        if (e.Button == MouseButtons.Left && i >= 0 && i < rows.Count && rows[i].Action != null) rows[i].Action();
    }
}

class HealthWindow : Form
{
    readonly Tray tray;
    readonly Health health = new Health();
    readonly Label verdict = new Label();
    readonly Panel scroll = new Panel();
    readonly HealthList list = new HealthList();
    readonly Timer refresh = new Timer();
    Counters counters;
    List<HealthRow> shown = new List<HealthRow>();

    public HealthWindow(Tray tray)
    {
        this.tray = tray;
        Text = "MemControl — why is it slow?";
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(740, Math.Min(748, Screen.PrimaryScreen.WorkingArea.Height - 60));
        MinimumSize = new Size(600, 360);
        BackColor = Color.FromArgb(248, 249, 251);
        const AnchorStyles sides = AnchorStyles.Left | AnchorStyles.Right;

        verdict.SetBounds(16, 12, ClientSize.Width - 32, 30);
        verdict.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        verdict.Anchor = AnchorStyles.Top | sides;
        scroll.SetBounds(16, 48, ClientSize.Width - 32, ClientSize.Height - 96);
        scroll.AutoScroll = true;
        scroll.BackColor = Color.White;
        scroll.BorderStyle = BorderStyle.FixedSingle;
        scroll.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | sides;
        list.SetBounds(0, 0, scroll.ClientSize.Width, HealthList.RowHeight);
        scroll.Controls.Add(list);
        scroll.Resize += (o, e) => list.Width = scroll.ClientSize.Width;
        var copy = new Button { Text = "Copy as text", Left = 16, Top = ClientSize.Height - 40, Width = 120, Height = 30, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        copy.Click += (o, e) => { try { Clipboard.SetText(Health.Report(shown)); } catch { } };
        var note = new Label { Text = "Click a line to act on it. Rest the pointer on a line to read all of it.", AutoSize = true, Left = 148, Top = ClientSize.Height - 33, ForeColor = Color.DimGray, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        Controls.AddRange(new Control[] { verdict, scroll, copy, note });

        refresh.Interval = 2000;
        refresh.Tick += (o, e) => { if (Visible) Fill(); };
        FormClosing += (o, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
            refresh.Stop();
            if (counters != null) { counters.Dispose(); counters = null; }
        };
    }

    /// <summary>Takes a reading and fills the lines. Percentages and rates appear from the second reading on.</summary>
    public void Prepare()
    {
        if (counters == null) { counters = new Counters(Health.Paths); counters.Collect(); }
        Fill();
    }

    public void Open()
    {
        Prepare();
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        refresh.Start();
    }

    void Fill()
    {
        if (counters == null) return;
        counters.Collect();
        SetRows(health.Collect(tray, counters));
    }

    /// <summary>Shows the given lines (also used to draw the window with example figures).</summary>
    public void SetRows(List<HealthRow> rows)
    {
        shown = rows;
        list.Width = scroll.ClientSize.Width;
        list.SetRows(rows);
        verdict.Text = Health.Verdict(rows);
        int worst = rows.Count > 0 ? rows.Max(r => r.Level) : 0;
        verdict.ForeColor = worst == 2 ? Color.FromArgb(200, 40, 50) : worst == 1 ? Color.FromArgb(190, 120, 0) : Color.FromArgb(30, 140, 70);
    }
}
