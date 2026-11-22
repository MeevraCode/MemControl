// Read-only probe for planning MemControl features: which measurements work on this PC without extra software.
// It changes nothing. The last step starts 200 empty processes to measure the kernel leak per process start.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

static class Probe
{
    [StructLayout(LayoutKind.Sequential)]
    struct PerfInfo
    {
        public int cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public int HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("psapi.dll")] static extern bool GetPerformanceInfo(out PerfInfo info, int size);
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("iphlpapi.dll")] static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQueryW(string source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterArrayW(IntPtr counter, int format, ref int size, out int count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
    [DllImport("powrprof.dll")] static extern int PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern int PowerReadFriendlyName(IntPtr root, IntPtr scheme, IntPtr sub, IntPtr setting, byte[] buffer, ref int size);
    [DllImport("powrprof.dll")] static extern int PowerReadACValueIndex(IntPtr root, IntPtr scheme, ref Guid sub, ref Guid setting, out int value);
    [DllImport("powrprof.dll")] static extern int PowerReadDCValueIndex(IntPtr root, IntPtr scheme, ref Guid sub, ref Guid setting, out int value);
    [DllImport("powrprof.dll")] static extern int PowerGetEffectiveOverlayScheme(out Guid overlay);
    [DllImport("powrprof.dll")] static extern int CallNtPowerInformation(int level, IntPtr input, int inputLength, IntPtr output, int outputLength);
    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(IntPtr data);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(IntPtr data);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref int adapter);

    static readonly string[] CounterPaths = {
        @"\Processor Information(_Total)\% Processor Utility",
        @"\Processor Information(_Total)\% Processor Performance",
        @"\Processor Information(_Total)\% of Maximum Frequency",
        @"\Processor Information(_Total)\% Performance Limit",
        @"\Processor Information(_Total)\% DPC Time",
        @"\Processor Information(_Total)\% Interrupt Time",
        @"\Thermal Zone Information(*)\Temperature",
        @"\Thermal Zone Information(*)\% Passive Limit",
        @"\Thermal Zone Information(*)\Throttle Reasons",
        @"\PhysicalDisk(_Total)\% Idle Time",
        @"\PhysicalDisk(_Total)\Avg. Disk Queue Length",
        @"\PhysicalDisk(_Total)\Disk Bytes/sec",
        @"\Memory\Pages Input/sec",
        @"\Memory\Standby Cache Normal Priority Bytes",
        @"\Memory\Standby Cache Reserve Bytes",
        @"\Memory\Standby Cache Core Bytes",
        @"\Memory\Modified Page List Bytes",
        @"\Memory\Free & Zero Page List Bytes",
        @"\Paging File(_Total)\% Usage",
        @"\GPU Adapter Memory(*)\Dedicated Usage",
        @"\GPU Adapter Memory(*)\Shared Usage",
        @"\GPU Engine(*)\Utilization Percentage",
        @"\GPU Process Memory(*)\Dedicated Usage",
        @"\GPU Process Memory(*)\Shared Usage",
    };

    class P { public string Name; public int Pid, Ppid, Handles, Threads, HardFaults; public long Private, PrivateWs, Ws, PagedPool, NonpagedPool, Read, Write, Create, Cpu; }
    class Tag { public string Name; public long Live, PagedBytes, NonpagedBytes; public long Bytes { get { return PagedBytes + NonpagedBytes; } } }

    static void Section(string title, Action body)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title);
        try { body(); }
        catch (Exception e) { Console.WriteLine("  FAILED: " + e.GetType().Name + ": " + e.Message); }
    }

    static string Mb(long bytes) { return (bytes / 1048576.0).ToString("N0") + " MB"; }
    static string Clean(string tag) { return new string(tag.Select(ch => ch < 32 || ch > 126 ? '.' : ch).ToArray()); }

    static IntPtr Query(int infoClass, int length)
    {
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(length);
            int returned;
            int status = NtQuerySystemInformation(infoClass, buffer, length, out returned);
            if (status == 0) return buffer;
            Marshal.FreeHGlobal(buffer);
            if (status == unchecked((int)0xC0000004) && length < (1 << 28)) { length = Math.Max(length * 2, returned + 65536); continue; }
            throw new Exception("NtQuerySystemInformation(" + infoClass + ") status 0x" + status.ToString("X8"));
        }
    }

    static List<P> Processes()
    {
        IntPtr buffer = Query(5, 1 << 20);
        try
        {
            var list = new List<P>();
            IntPtr e = buffer;
            while (true)
            {
                int next = Marshal.ReadInt32(e);
                IntPtr namePtr = Marshal.ReadIntPtr(e, 64);
                int nameLen = (ushort)Marshal.ReadInt16(e, 56);
                list.Add(new P {
                    Threads = Marshal.ReadInt32(e, 4), PrivateWs = Marshal.ReadInt64(e, 8), HardFaults = Marshal.ReadInt32(e, 16), Create = Marshal.ReadInt64(e, 32),
                    Cpu = Marshal.ReadInt64(e, 40) + Marshal.ReadInt64(e, 48),
                    Name = namePtr == IntPtr.Zero ? "Idle" : Marshal.PtrToStringUni(namePtr, nameLen / 2),
                    Pid = (int)Marshal.ReadInt64(e, 80), Ppid = (int)Marshal.ReadInt64(e, 88), Handles = Marshal.ReadInt32(e, 96),
                    Ws = Marshal.ReadInt64(e, 144), PagedPool = Marshal.ReadInt64(e, 160), NonpagedPool = Marshal.ReadInt64(e, 176),
                    Private = Marshal.ReadInt64(e, 200), Read = Marshal.ReadInt64(e, 232), Write = Marshal.ReadInt64(e, 240),
                });
                if (next == 0) break;
                e = e + next;
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static List<Tag> Tags()
    {
        IntPtr buffer = Query(22, 1 << 20);
        try
        {
            int count = Marshal.ReadInt32(buffer);
            var list = new List<Tag>(count);
            var chars = new byte[4];
            for (int i = 0; i < count; i++)
            {
                IntPtr e = buffer + 8 + i * 40;
                Marshal.Copy(e, chars, 0, 4);
                long paged = (uint)(Marshal.ReadInt32(e, 4) - Marshal.ReadInt32(e, 8)), nonpaged = (uint)(Marshal.ReadInt32(e, 24) - Marshal.ReadInt32(e, 28));
                list.Add(new Tag { Name = Encoding.ASCII.GetString(chars), Live = paged + nonpaged, PagedBytes = Marshal.ReadInt64(e, 16), NonpagedBytes = Marshal.ReadInt64(e, 32) });
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static string CommandLine(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int needed;
            NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out needed);
            if (needed <= 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(h, 60, buffer, needed, out needed) != 0) return null;
                int length = (ushort)Marshal.ReadInt16(buffer);
                IntPtr text = Marshal.ReadIntPtr(buffer, 8);
                return text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(h); }
    }

    // Only the script or module name (a plain file name), never the whole command line: it can hold secrets.
    static string ArgSummary(string cmd)
    {
        var args = new List<string>(); var cur = new StringBuilder(); bool quoted = false;
        foreach (char ch in cmd)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ' ' && !quoted) { if (cur.Length > 0) { args.Add(cur.ToString()); cur.Length = 0; } continue; }
            cur.Append(ch);
        }
        if (cur.Length > 0) args.Add(cur.ToString());
        foreach (var a in args.Skip(1))
        {
            if (a.StartsWith("-") || a.StartsWith("/")) continue;
            string name = a; int cut = name.LastIndexOfAny(new[] { '\\', '/' }); if (cut >= 0) name = name.Substring(cut + 1);
            if (name.Length == 0 || name.Length > 40 || !name.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_' || ch == '@')) return "(other)";
            return name;
        }
        return args.Count > 1 ? "(flags only)" : "(no arguments)";
    }

    static List<KeyValuePair<int, int>> Listeners(int family)
    {
        var result = new List<KeyValuePair<int, int>>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        if (size == 0) return result;
        size += 4096;
        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            int rc = GetExtendedTcpTable(table, ref size, false, family, 3, 0);
            if (rc != 0) throw new Exception("GetExtendedTcpTable returned " + rc);
            int count = Marshal.ReadInt32(table);
            int row = family == 2 ? 24 : 56, portAt = family == 2 ? 8 : 20, pidAt = family == 2 ? 20 : 52;
            for (int i = 0; i < count; i++)
            {
                IntPtr r = table + 4 + i * row;
                int raw = Marshal.ReadInt32(r, portAt);
                result.Add(new KeyValuePair<int, int>(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF), Marshal.ReadInt32(r, pidAt)));
            }
        }
        finally { Marshal.FreeHGlobal(table); }
        return result;
    }

    static Dictionary<string, List<KeyValuePair<string, double>>> Pdh(string[] paths)
    {
        var result = new Dictionary<string, List<KeyValuePair<string, double>>>();
        IntPtr query;
        int rc = PdhOpenQueryW(null, IntPtr.Zero, out query);
        if (rc != 0) { Console.WriteLine("  PdhOpenQuery failed 0x" + rc.ToString("X8")); return result; }
        var counters = new List<KeyValuePair<string, IntPtr>>();
        foreach (var path in paths)
        {
            IntPtr counter;
            rc = PdhAddEnglishCounterW(query, path, IntPtr.Zero, out counter);
            if (rc != 0) Console.WriteLine("    NOT AVAILABLE " + path + " (0x" + rc.ToString("X8") + ")");
            else counters.Add(new KeyValuePair<string, IntPtr>(path, counter));
        }
        PdhCollectQueryData(query);
        Thread.Sleep(1500);
        PdhCollectQueryData(query);
        foreach (var kv in counters)
        {
            var values = new List<KeyValuePair<string, double>>();
            int size = 0, count;
            rc = PdhGetFormattedCounterArrayW(kv.Value, 0x200, ref size, out count, IntPtr.Zero);
            if (rc == unchecked((int)0x800007D2) && size > 0)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    rc = PdhGetFormattedCounterArrayW(kv.Value, 0x200, ref size, out count, buffer);
                    if (rc == 0)
                        for (int i = 0; i < count; i++)
                        {
                            IntPtr item = buffer + i * 24;
                            int cstatus = Marshal.ReadInt32(item, 8);
                            if (cstatus != 0 && cstatus != 1) continue;
                            values.Add(new KeyValuePair<string, double>(Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "", BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16))));
                        }
                    else Console.WriteLine("    read failed " + kv.Key + " (0x" + rc.ToString("X8") + ")");
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            else Console.WriteLine("    no data " + kv.Key + " (0x" + rc.ToString("X8") + ")");
            result[kv.Key] = values;
        }
        PdhCloseQuery(query);
        return result;
    }

    static List<string> LoadedModules()
    {
        IntPtr buffer = Query(11, 1 << 18);
        try
        {
            int count = Marshal.ReadInt32(buffer);
            var list = new List<string>();
            for (int i = 0; i < count; i++) list.Add(Marshal.PtrToStringAnsi(buffer + 8 + i * 296 + 40));
            return list;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static List<string> DriversFromRegistry()
    {
        var list = new List<string>();
        using (var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
            foreach (var name in services.GetSubKeyNames())
                try
                {
                    using (var k = services.OpenSubKey(name))
                    {
                        if (k == null) continue;
                        object type = k.GetValue("Type"); string image = k.GetValue("ImagePath") as string;
                        if (!(type is int) || ((int)type != 1 && (int)type != 2)) continue;
                        list.Add(string.IsNullOrEmpty(image) ? @"\SystemRoot\System32\drivers\" + name + ".sys" : image);
                    }
                }
                catch { }
        return list;
    }

    static string Resolve(string p)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        p = Environment.ExpandEnvironmentVariables(p);
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(win, p.Substring(12));
        if (p.StartsWith(@"\??\")) return p.Substring(4);
        if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(win, p);
        if (p.StartsWith(@"\Windows\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(Path.GetPathRoot(win), p.Substring(1));
        if (p.IndexOf('\\') < 0) return Path.Combine(win, @"System32\drivers", p);
        return p;
    }

    static bool HasBytes(byte[] data, byte[] tag)
    {
        byte a = tag[0], b = tag[1], c = tag[2], d = tag[3];
        int end = data.Length - 3;
        for (int i = 0; i < end; i++) if (data[i] == a && data[i + 1] == b && data[i + 2] == c && data[i + 3] == d) return true;
        return false;
    }

    static string Elevation(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return "cannot open the process";
        try
        {
            IntPtr token;
            if (!OpenProcessToken(h, 0x8, out token)) return "cannot read its token (usually: it is elevated and this probe is not)";
            try { int elevated, returned; return GetTokenInformation(token, 20, out elevated, 4, out returned) ? (elevated != 0 ? "elevated" : "not elevated") : "unknown"; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(h); }
    }

    static int Main()
    {
        List<P> before = null, after = null;
        List<Tag> tags = null;
        Dictionary<string, List<KeyValuePair<string, double>>> pdh = null;

        Section("Basics", () =>
        {
            bool admin;
            using (var id = WindowsIdentity.GetCurrent()) admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            Console.WriteLine("  this probe elevated: " + admin + " | 64-bit: " + (IntPtr.Size == 8) + " | logical CPUs: " + Environment.ProcessorCount + " | OS: " + Environment.OSVersion.Version);
            Console.WriteLine("  uptime by tick count: " + (GetTickCount64() / 86400000.0).ToString("0.0") + " days");
            IntPtr tod = Marshal.AllocHGlobal(64);
            try
            {
                int returned;
                int status = NtQuerySystemInformation(3, tod, 48, out returned);
                Console.WriteLine(status == 0 ? "  kernel boot time: " + DateTime.FromFileTime(Marshal.ReadInt64(tod)).ToString("yyyy-MM-dd HH:mm") : "  boot time status 0x" + status.ToString("X8"));
            }
            finally { Marshal.FreeHGlobal(tod); }
            object fast = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", null);
            Console.WriteLine("  Fast Startup (HiberbootEnabled): " + (fast == null ? "not set" : fast.ToString()));
            bool wu, cbs;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) wu = k != null;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")) cbs = k != null;
            Console.WriteLine("  restart pending: Windows Update " + wu + ", servicing " + cbs);
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed && d.IsReady) Console.WriteLine("  drive " + d.Name + " free " + (d.AvailableFreeSpace / 1073741824.0).ToString("0.0") + " of " + (d.TotalSize / 1073741824.0).ToString("0.0") + " GB");
            int state;
            int hr = SHQueryUserNotificationState(out state);
            Console.WriteLine("  notification state: " + state + " (hr " + hr + "; 2 = full-screen app, 3 = Direct3D full screen, 4 = presentation, 5 = normal)");
        });

        Section("Process snapshot (one NtQuerySystemInformation call) and performance counters (PDH, English names)", () =>
        {
            before = Processes();
            var sw = Stopwatch.StartNew();
            pdh = Pdh(CounterPaths);
            after = Processes();
            double seconds = sw.Elapsed.TotalSeconds;
            Console.WriteLine("  processes: " + after.Count + " | threads: " + after.Sum(p => p.Threads).ToString("N0") + " | handles: " + after.Sum(p => (long)p.Handles).ToString("N0"));
            using (var me = Process.GetCurrentProcess())
            {
                var mine = after.First(p => p.Pid == me.Id);
                Console.WriteLine("  self-check against .NET: private " + Mb(mine.Private) + " vs " + Mb(me.PrivateMemorySize64) + " | handles " + mine.Handles + " vs " + me.HandleCount + " | name " + mine.Name);
            }
            var prev = before.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
            Func<P, double> cpu = p => { P b; return prev.TryGetValue(p.Pid, out b) && b.Create == p.Create ? (p.Cpu - b.Cpu) / 1e7 / seconds / Environment.ProcessorCount * 100 : 0; };
            Func<P, double> io = p => { P b; return prev.TryGetValue(p.Pid, out b) && b.Create == p.Create ? (p.Read + p.Write - b.Read - b.Write) / seconds / 1048576.0 : 0; };
            var real = after.Where(p => p.Pid != 0).ToList();
            Console.WriteLine("  top CPU now: " + string.Join(", ", real.OrderByDescending(cpu).Take(4).Select(p => p.Name + " " + cpu(p).ToString("0.0") + "%")));
            Console.WriteLine("  top disk I/O now: " + string.Join(", ", real.OrderByDescending(io).Take(4).Select(p => p.Name + " " + io(p).ToString("0.00") + " MB/s")));
            Console.WriteLine("  top private bytes: " + string.Join(", ", real.OrderByDescending(p => p.Private).Take(5).Select(p => p.Name + " " + Mb(p.Private))));
            Console.WriteLine("  top handles: " + string.Join(", ", real.OrderByDescending(p => p.Handles).Take(5).Select(p => p.Name + " " + p.Handles.ToString("N0"))));
            Console.WriteLine("  top hard faults since start: " + string.Join(", ", real.OrderByDescending(p => (uint)p.HardFaults).Take(4).Select(p => p.Name + " " + ((uint)p.HardFaults).ToString("N0"))));
            PerfInfo pi;
            GetPerformanceInfo(out pi, Marshal.SizeOf(typeof(PerfInfo)));
            long page = (long)pi.PageSize.ToUInt64();
            Console.WriteLine("  RAM: total " + Mb((long)pi.PhysicalTotal.ToUInt64() * page) + ", available " + Mb((long)pi.PhysicalAvailable.ToUInt64() * page) + " | commit " + Mb((long)pi.CommitTotal.ToUInt64() * page) + " of " + Mb((long)pi.CommitLimit.ToUInt64() * page));
            Console.WriteLine("  sum of private working sets of all processes: " + Mb(real.Sum(p => p.PrivateWs)) + " | sum of private bytes: " + Mb(real.Sum(p => p.Private)));
            Console.WriteLine("  kernel pool: paged " + Mb((long)pi.KernelPaged.ToUInt64() * page) + ", nonpaged " + Mb((long)pi.KernelNonpaged.ToUInt64() * page)
                + " | charged to running processes: paged " + Mb(real.Sum(p => p.PagedPool)) + ", nonpaged " + Mb(real.Sum(p => p.NonpagedPool)));
            foreach (var p in real.Where(p => p.Name.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase) || p.Name.IndexOf("Compression", StringComparison.OrdinalIgnoreCase) >= 0))
                Console.WriteLine("  special: " + p.Name + " working set " + Mb(p.Ws) + ", private " + Mb(p.Private));

            Console.WriteLine("  counters:");
            foreach (var path in CounterPaths)
            {
                List<KeyValuePair<string, double>> v;
                if (!pdh.TryGetValue(path, out v)) continue;
                if (v.Count == 0) { Console.WriteLine("    (no values) " + path); continue; }
                if (path.EndsWith(@"\Temperature")) Console.WriteLine("    " + path + ": " + string.Join(" | ", v.Select(i => i.Key + " = " + (i.Value - 273.15).ToString("0.0") + " C")));
                else if (path.Contains("Thermal Zone")) Console.WriteLine("    " + path + ": " + string.Join(" | ", v.Select(i => i.Key + " = " + i.Value.ToString("0.#"))));
                else if (path.Contains("GPU Adapter")) Console.WriteLine("    " + path + ": " + string.Join(" | ", v.Select(i => Mb((long)i.Value))));
                else if (v.Count == 1) Console.WriteLine("    " + path + " = " + v[0].Value.ToString("N1"));
                else Console.WriteLine("    " + path + ": " + v.Count + " instances, sum " + v.Sum(i => i.Value).ToString("N1") + ", max " + v.Max(i => i.Value).ToString("N1"));
            }
            Func<string, int> pidOf = name => { var parts = name.Split('_'); int id; return parts.Length > 1 && parts[0] == "pid" && int.TryParse(parts[1], out id) ? id : -1; };
            var names = after.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Name);
            Func<int, string> nameOf = id => { string n; return names.TryGetValue(id, out n) ? n : "pid " + id; };
            List<KeyValuePair<string, double>> gpu;
            if (pdh.TryGetValue(@"\GPU Engine(*)\Utilization Percentage", out gpu) && gpu.Count > 0)
                Console.WriteLine("  GPU by process (3D engines): " + string.Join(", ", gpu.Where(i => i.Key.EndsWith("engtype_3D")).GroupBy(i => pidOf(i.Key)).Select(g => new { Pid = g.Key, Sum = g.Sum(i => i.Value) }).OrderByDescending(g => g.Sum).Take(4).Select(g => nameOf(g.Pid) + " " + g.Sum.ToString("0.0") + "%")));
            foreach (var kind in new[] { "Dedicated", "Shared" })
                if (pdh.TryGetValue(@"\GPU Process Memory(*)\" + kind + " Usage", out gpu) && gpu.Count > 0)
                    Console.WriteLine("  GPU memory by process (" + kind.ToLower() + "): " + string.Join(", ", gpu.GroupBy(i => pidOf(i.Key)).Select(g => new { Pid = g.Key, Sum = g.Sum(i => i.Value) }).OrderByDescending(g => g.Sum).Take(4).Select(g => nameOf(g.Pid) + " " + Mb((long)g.Sum))));
        });

        Section("Orphans and command lines", () =>
        {
            var byPid = after.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
            var orphans = after.Where(p => { P parent; return p.Pid > 4 && (!byPid.TryGetValue(p.Ppid, out parent) || parent.Create > p.Create); }).ToList();
            Console.WriteLine("  processes whose parent is gone: " + orphans.Count + " of " + after.Count);
            var dev = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node.exe", "python.exe", "pythonw.exe", "python3.exe", "bun.exe", "deno.exe", "java.exe", "javaw.exe", "dotnet.exe", "esbuild.exe", "cargo.exe", "rust-analyzer.exe", "go.exe", "php.exe", "ruby.exe", "uv.exe", "adb.exe", "msbuild.exe", "VBCSCompiler.exe", "git.exe", "claude.exe" };
            var devAll = after.Where(p => dev.Contains(p.Name)).ToList();
            var devOrphans = orphans.Where(p => dev.Contains(p.Name)).ToList();
            Console.WriteLine("  developer-tool processes: " + devAll.Count + " holding " + Mb(devAll.Sum(p => p.Private)) + " -> " + string.Join(", ", devAll.GroupBy(p => p.Name.ToLowerInvariant()).OrderByDescending(g => g.Count()).Select(g => g.Key + " x" + g.Count())));
            Console.WriteLine("  of them with the parent gone: " + devOrphans.Count + " holding " + Mb(devOrphans.Sum(p => p.Private)) + " -> " + string.Join(", ", devOrphans.GroupBy(p => p.Name.ToLowerInvariant()).Select(g => g.Key + " x" + g.Count())));
            int read = 0;
            var kinds = new Dictionary<string, long[]>();
            foreach (var p in devAll)
            {
                string cmd = CommandLine(p.Pid);
                if (cmd == null) continue;
                read++;
                string key = p.Name.ToLowerInvariant() + " " + ArgSummary(cmd);
                long[] v;
                if (!kinds.TryGetValue(key, out v)) kinds[key] = v = new long[2];
                v[0]++; v[1] += p.Private;
            }
            Console.WriteLine("  command line readable for " + read + " of " + devAll.Count + "; what they run (script name only):");
            foreach (var kv in kinds.OrderByDescending(k => k.Value[1]).Take(14)) Console.WriteLine("    " + kv.Value[0] + " x " + kv.Key + "  " + Mb(kv.Value[1]));
        });

        Section("Listening TCP ports with their owner", () =>
        {
            var names = after.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Name);
            var all = Listeners(2).Concat(Listeners(23)).GroupBy(kv => kv.Key + ":" + kv.Value).Select(g => g.First()).ToList();
            Func<int, string> nameOf = id => { string n; return names.TryGetValue(id, out n) ? n : "pid " + id; };
            Console.WriteLine("  listening ports: " + all.Count + " -> " + string.Join(", ", all.OrderBy(kv => kv.Key).Take(40).Select(kv => kv.Key + " " + nameOf(kv.Value))));
        });

        Section("GDI and USER objects of another process", () =>
        {
            var ex = after.FirstOrDefault(p => p.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase));
            if (ex == null) { Console.WriteLine("  explorer.exe not found"); return; }
            foreach (int access in new[] { 0x1000, 0x0400 })
            {
                IntPtr h = OpenProcess(access, false, ex.Pid);
                if (h == IntPtr.Zero) { Console.WriteLine("  OpenProcess(0x" + access.ToString("X") + ") failed"); continue; }
                Console.WriteLine("  explorer.exe with access 0x" + access.ToString("X") + ": GDI " + GetGuiResources(h, 0) + ", USER " + GetGuiResources(h, 1));
                CloseHandle(h);
            }
        });

        Section("Kernel pool by tag", () =>
        {
            tags = Tags();
            Console.WriteLine("  tags: " + tags.Count + " | all tags together: paged " + Mb(tags.Sum(t => t.PagedBytes)) + ", nonpaged " + Mb(tags.Sum(t => t.NonpagedBytes)));
            foreach (var t in tags.OrderByDescending(t => t.Bytes).Take(16))
                Console.WriteLine(string.Format("    {0}  paged {1,7:N0} MB  nonpaged {2,7:N0} MB  live {3,12:N0}  ({4:N0} bytes each)", Clean(t.Name), t.PagedBytes / 1048576.0, t.NonpagedBytes / 1048576.0, t.Live, t.Live > 0 ? t.Bytes / (double)t.Live : 0));
            var proc = tags.FirstOrDefault(t => t.Name == "Proc"); var thre = tags.FirstOrDefault(t => t.Name == "Thre");
            if (proc != null && after != null) Console.WriteLine("  process objects alive in the kernel: " + proc.Live.ToString("N0") + " | running processes: " + after.Count + " | difference (zombies): " + (proc.Live - after.Count).ToString("N0") + " | tag bytes " + Mb(proc.Bytes));
            if (thre != null && after != null) Console.WriteLine("  thread objects alive in the kernel: " + thre.Live.ToString("N0") + " | running threads: " + after.Sum(p => p.Threads).ToString("N0") + " | tag bytes " + Mb(thre.Bytes));
        });

        Section("Tag -> driver lookup (kernel module files scanned for the tag text)", () =>
        {
            List<string> modules;
            try { modules = LoadedModules(); Console.WriteLine("  loaded kernel modules (NtQuerySystemInformation 11): " + modules.Count); }
            catch (Exception e) { Console.WriteLine("  loaded-module list not available (" + e.Message + "); using the registry list of drivers"); modules = DriversFromRegistry(); Console.WriteLine("  drivers in the registry: " + modules.Count); }
            var wanted = tags.OrderByDescending(t => t.Bytes).Take(12).Select(t => t.Name).Distinct().ToList();
            var hits = wanted.ToDictionary(w => w, w => new List<string>());
            var sw = Stopwatch.StartNew(); long scanned = 0; int unreadable = 0, files = 0;
            foreach (var path in modules.Select(Resolve).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                byte[] data;
                try { data = File.ReadAllBytes(path); } catch { unreadable++; continue; }
                files++; scanned += data.Length;
                foreach (var w in wanted) if (HasBytes(data, Encoding.ASCII.GetBytes(w))) hits[w].Add(Path.GetFileName(path));
            }
            Console.WriteLine("  scanned " + files + " files, " + Mb(scanned) + " in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s | unreadable: " + unreadable);
            foreach (var w in wanted) Console.WriteLine("    " + Clean(w) + " -> " + (hits[w].Count == 0 ? "no module" : hits[w].Count > 6 ? hits[w].Count + " modules (too common to attribute)" : string.Join(", ", hits[w])));
        });

        Section("Power", () =>
        {
            PowerStatus ps;
            if (GetSystemPowerStatus(out ps)) Console.WriteLine("  plugged in: " + ps.ACLineStatus + " | battery flag: " + ps.BatteryFlag + " (128 = no battery) | battery: " + ps.BatteryLifePercent + "% | battery saver: " + ps.SystemStatusFlag);
            IntPtr scheme;
            if (PowerGetActiveScheme(IntPtr.Zero, out scheme) == 0)
            {
                var buffer = new byte[1024]; int size = buffer.Length;
                string name = PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0 ? Encoding.Unicode.GetString(buffer, 0, Math.Max(0, size - 2)) : "?";
                Console.WriteLine("  active power plan: " + name + " " + (Guid)Marshal.PtrToStructure(scheme, typeof(Guid)));
                Guid sub = new Guid("54533251-82be-4824-96c1-47b60b740d00"), max = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
                int ac, dc;
                if (PowerReadACValueIndex(IntPtr.Zero, scheme, ref sub, ref max, out ac) == 0 && PowerReadDCValueIndex(IntPtr.Zero, scheme, ref sub, ref max, out dc) == 0)
                    Console.WriteLine("  maximum processor state: plugged in " + ac + "% | on battery " + dc + "%");
                else Console.WriteLine("  maximum processor state: not readable");
                Marshal.FreeHGlobal(scheme);
            }
            Guid overlay;
            int rc = PowerGetEffectiveOverlayScheme(out overlay);
            Console.WriteLine("  power mode: " + (rc == 0 ? overlay.ToString() : "failed " + rc) + " (961cc777 = best power efficiency, 00000000 = balanced, ded574b5 = best performance)");
            int n = Environment.ProcessorCount;
            IntPtr info = Marshal.AllocHGlobal(n * 24);
            try
            {
                rc = CallNtPowerInformation(11, IntPtr.Zero, 0, info, n * 24);
                if (rc != 0) { Console.WriteLine("  processor power information status 0x" + rc.ToString("X8")); return; }
                var current = Enumerable.Range(0, n).Select(i => Marshal.ReadInt32(info, i * 24 + 8)).ToList();
                Console.WriteLine("  CPU MHz: maximum " + Marshal.ReadInt32(info, 4) + " | current " + current.Min() + " to " + current.Max() + " | limit " + Marshal.ReadInt32(info, 12));
            }
            finally { Marshal.FreeHGlobal(info); }
        });

        Section("GPU temperature (D3DKMT adapter performance data, as Task Manager reads it)", () =>
        {
            IntPtr en = Marshal.AllocHGlobal(16);
            Marshal.WriteInt64(en, 0, 0); Marshal.WriteInt64(en, 8, 0);
            int status = D3DKMTEnumAdapters2(en);
            int n = Marshal.ReadInt32(en);
            if (status != 0 || n <= 0 || n > 64) { Console.WriteLine("  adapter count query: status 0x" + status.ToString("X8") + ", count " + n); return; }
            IntPtr array = Marshal.AllocHGlobal(n * 20);
            Marshal.WriteInt32(en, 0, n); Marshal.WriteIntPtr(en, 8, array);
            status = D3DKMTEnumAdapters2(en);
            if (status != 0) { Console.WriteLine("  enumeration failed 0x" + status.ToString("X8")); return; }
            n = Marshal.ReadInt32(en);
            for (int i = 0; i < n; i++)
            {
                int handle = Marshal.ReadInt32(array, i * 20);
                IntPtr perf = Marshal.AllocHGlobal(64), query = Marshal.AllocHGlobal(24);
                for (int k = 0; k < 64; k++) Marshal.WriteByte(perf, k, 0);
                Marshal.WriteInt32(query, 0, handle); Marshal.WriteInt32(query, 4, 62); Marshal.WriteIntPtr(query, 8, perf); Marshal.WriteInt32(query, 16, 64); Marshal.WriteInt32(query, 20, 0);
                status = D3DKMTQueryAdapterInfo(query);
                Console.WriteLine(string.Format("  adapter {0}: status 0x{1:X8} | temperature {2:0.0} C | fan {3} rpm | power {4:0.0} %", i, status, (uint)Marshal.ReadInt32(perf, 56) / 10.0, (uint)Marshal.ReadInt32(perf, 48), (uint)Marshal.ReadInt32(perf, 52) / 10.0));
                Marshal.FreeHGlobal(query); Marshal.FreeHGlobal(perf);
                D3DKMTCloseAdapter(ref handle);
            }
            Marshal.FreeHGlobal(array); Marshal.FreeHGlobal(en);
        });

        Section("Memory lists by direct query (expected to need administrator rights)", () =>
        {
            IntPtr b = Marshal.AllocHGlobal(176);
            try
            {
                int returned;
                int status = NtQuerySystemInformation(80, b, 176, out returned);
                if (status != 0) { Console.WriteLine("  status 0x" + status.ToString("X8") + " (0xC0000061 = privilege not held)"); return; }
                long page = Environment.SystemPageSize, standby = 0;
                for (int i = 0; i < 8; i++) standby += Marshal.ReadInt64(b, 40 + i * 8);
                Console.WriteLine("  zero " + Mb(Marshal.ReadInt64(b, 0) * page) + ", free " + Mb(Marshal.ReadInt64(b, 8) * page) + ", modified " + Mb(Marshal.ReadInt64(b, 16) * page) + ", standby " + Mb(standby * page));
            }
            finally { Marshal.FreeHGlobal(b); }
        });

        Section("Startup programs", () =>
        {
            string run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            var items = new List<string>();
            using (var k = Registry.CurrentUser.OpenSubKey(run)) if (k != null) items.AddRange(k.GetValueNames().Select(n => "user: " + n));
            using (var k = Registry.LocalMachine.OpenSubKey(run)) if (k != null) items.AddRange(k.GetValueNames().Select(n => "machine: " + n));
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")) if (k != null) items.AddRange(k.GetValueNames().Select(n => "machine32: " + n));
            foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
            {
                string dir = Environment.GetFolderPath(folder);
                if (Directory.Exists(dir)) items.AddRange(Directory.GetFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)).Select(f => "folder: " + Path.GetFileNameWithoutExtension(f)));
            }
            Console.WriteLine("  " + items.Count + " startup entries: " + string.Join(" | ", items));
            foreach (var where in new[] { "Run", "Run32", "StartupFolder" })
                using (var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + where))
                    if (k != null) Console.WriteLine("  StartupApproved\\" + where + " (user): " + k.GetValueNames().Length + " entries, switched off: " + k.GetValueNames().Count(n => { var b = k.GetValue(n) as byte[]; return b != null && b.Length > 0 && (b[0] & 1) == 1; }));
        });

        Section("MemControl itself", () =>
        {
            var mc = after.Where(p => p.Name.Equals("MemControl.exe", StringComparison.OrdinalIgnoreCase)).ToList();
            if (mc.Count == 0) { Console.WriteLine("  not running"); return; }
            foreach (var p in mc) Console.WriteLine("  pid " + p.Pid + ": " + Elevation(p.Pid) + " | private " + Mb(p.Private) + " | handles " + p.Handles + " | running since " + DateTime.FromFileTime(p.Create).ToString("yyyy-MM-dd HH:mm"));
        });

        Section("Leak self-test: live 'File' objects per process start", () =>
        {
            string noop = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "noop.exe");
            if (!File.Exists(noop)) { Console.WriteLine("  noop.exe is missing"); return; }
            Func<long> live = () => { var f = Tags().FirstOrDefault(t => t.Name == "File"); return f == null ? -1 : f.Live; };
            long a0 = live(); Thread.Sleep(5000); long a1 = live();
            const int starts = 200;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < starts; i++) using (var p = Process.Start(new ProcessStartInfo(noop) { UseShellExecute = false })) p.WaitForExit();
            sw.Stop();
            Thread.Sleep(1500);
            long b = live(); Thread.Sleep(5000); long c = live();
            Console.WriteLine("  live File objects: " + a1.ToString("N0") + " | idle 5 s before: " + (a1 - a0) + " | during " + starts + " process starts (" + sw.Elapsed.TotalSeconds.ToString("0.0") + " s): +" + (b - a1) + " | idle 5 s after: " + (c - b));
            Console.WriteLine("  -> " + ((b - a1) / (double)starts).ToString("0.00") + " File objects stay behind per process start");
        });

        return 0;
    }
}
