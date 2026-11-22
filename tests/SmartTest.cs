// Checks MemControl's smart cleaning, its health and leak figures and the plumbing of "Always run as administrator",
// without touching your programs: every cleaning here runs on throwaway processes only.
// Build and run: tests\run-tests.cmd (exit code 0 = all passed).
//
// Other modes:
//   SmartTest.exe /admin-test MEMCONTROL.EXE   the one check that needs administrator rights. One UAC prompt appears
//                                             (for SmartTest.exe); the rest runs by itself and is undone at the end.
//   SmartTest.exe /shots FOLDER [demo]        draws the windows off screen into pictures (demo: with example figures)
//   SmartTest.exe /auto FOLDER                one gentle automatic cleaning through the real path, nothing is touched
//   SmartTest.exe /task create NAME EXE ARGS  a least-privilege scheduled task; /task delete NAME removes it
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

static class SmartTest
{
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);

    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "/child") return Child(args[1]);
        if (args.Length >= 2 && args[0] == "/shots") return Shots(args[1], args.Length > 2 && args[2] == "demo");
        if (args.Length == 2 && args[0] == "/auto") return Auto(args[1]);
        if (args.Length >= 3 && args[0] == "/task") return TaskCommand(args);
        if (args.Length == 2 && args[0] == "/admin-test") return AdminTest(args[1]);
        if (args.Length == 6 && args[0] == "/admin-helper") return AdminHelper(args);
        Console.WriteLine("INFO this account can use \"Always run as administrator\": " + AdminTask.Possible());
        Policy();
        Lists();
        Snapshot();
        Busy();
        Trim();
        Commit();
        SharedObjects();
        ScheduledTask();
        ProtectedCopy();
        PlainUser();
        Figures();
        History();
        Leaks();
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }

    static void Policy()
    {
        var p = new AutoPolicy();
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        Check(!p.Due(t, 40, 50), "policy: under the limit nothing is due");
        Check(p.Due(t.AddSeconds(2), 60, 50), "policy: over the limit the first cleaning is due at once");
        p.Done(t, 60);
        Check(!p.Due(t.AddMinutes(1), 60, 50), "policy: not within 2 min of the last cleaning");
        Check(!p.Due(t.AddMinutes(4), 62, 50), "policy: after one cleaning the next waits 5 min");
        Check(p.Due(t.AddMinutes(5), 62, 50), "policy: due after 5 min");
        p.Done(t.AddMinutes(5), 62);
        Check(p.Wait == 15, "policy: after two cleanings the wait is 15 min (it is " + p.Wait + ")");
        Check(!p.Due(t.AddMinutes(6), 75, 50), "policy: a jump of 13 points does not beat the 2-minute floor");
        Check(p.Due(t.AddMinutes(8), 75, 50), "policy: a jump of 13 points cuts the wait after 2 min");
        Check(!p.Due(t.AddMinutes(8), 70, 50), "policy: a rise of 8 points does not");
        Check(!p.Due(t.AddMinutes(19), 62, 50), "policy: still waiting at 14 min");
        Check(p.Due(t.AddMinutes(20), 62, 50), "policy: due after 15 min");
        p.Done(t.AddMinutes(20), 62);
        p.Done(t.AddMinutes(50), 62);
        p.Done(t.AddMinutes(80), 62);
        Check(p.Wait == 30, "policy: the wait stops growing at 30 min (it is " + p.Wait + ")");
        Check(p.Next == t.AddMinutes(110), "policy: the next cleaning is expected 30 min after the last");
        Check(!p.Due(t.AddMinutes(82), 40, 50) && !p.Due(t.AddMinutes(91), 40, 50), "policy: under the limit nothing is due");
        Check(p.Wait == 30, "policy: 9 calm minutes do not start it over");
        p.Due(t.AddMinutes(93), 40, 50);
        Check(p.Wait == 2, "policy: 10 calm minutes start it over (the wait is " + p.Wait + ")");
        Check(p.Due(t.AddMinutes(94), 55, 50), "policy: and the next crossing is cleaned at once");
        var q = new AutoPolicy();
        q.Done(t, 60);
        q.Done(t.AddMinutes(5), 60);
        q.Reset();
        Check(q.Wait == 2 && !q.Due(t.AddMinutes(6), 60, 50) && q.Due(t.AddMinutes(7), 60, 50), "policy: a changed limit starts over but keeps the 2-minute floor");
    }

    static void Lists()
    {
        var set = ProgramList.Parse(" Game.exe, obs64 ; \"Chrome.EXE\" ,, game ");
        Check(set.Count == 3 && set.Contains("GAME") && set.Contains("chrome") && set.Contains("obs64"), "list: names are read without .exe, quotes, case or repeats (" + ProgramList.Join(set) + ")");
        Check(ProgramList.Join(set) == "Chrome, Game, obs64", "list: written back sorted");
        Check(ProgramList.Parse(ProgramList.Join(set)).SetEquals(set), "list: reading what was written gives the same list");
        Check(ProgramList.Parse(null).Count == 0 && ProgramList.Parse("  ,; ").Count == 0, "list: an empty text is an empty list");
        var options = new CleanOptions(set);
        options.SkipPids.Add(77);
        Check(options.Skips(new ProcInfo { Pid = 5, Name = "chrome" }) && options.Skips(new ProcInfo { Pid = 77, Name = "other" }) && !options.Skips(new ProcInfo { Pid = 6, Name = "notepad" }),
            "options: a listed name and a listed process are left alone, others are not");
        Check(!new CleanOptions().Any && options.Any, "options: empty options leave nothing alone");
    }

    static void Snapshot()
    {
        var all = Sys.Processes();
        using (var me = Process.GetCurrentProcess())
        {
            var mine = all.FirstOrDefault(p => p.Pid == me.Id);
            Check(all.Count > 20, "snapshot: Windows listed " + all.Count + " processes in one call");
            Check(mine != null && mine.Name == me.ProcessName, "snapshot: this process is in it under the name " + (mine == null ? "?" : mine.Name));
            if (mine == null) return;
            double ratio = mine.PrivateBytes / (double)me.PrivateMemorySize64;
            Check(ratio > 0.8 && ratio < 1.25, "snapshot: its private memory matches .NET's figure (" + mine.PrivateBytes / 1048576 + " and " + me.PrivateMemorySize64 / 1048576 + " MB)");
            Check(mine.ParentPid > 0 && mine.Created > 0 && mine.Threads > 0 && mine.Handles > 0, "snapshot: parent, start time, threads and handles are filled in");
            string command = Sys.CommandLine(me.Id);
            Check(command != null && command.IndexOf("SmartTest", StringComparison.OrdinalIgnoreCase) >= 0, "snapshot: the command line of a process can be read");
        }
        Check(all.Any(p => p.Pid == 4 && p.Name == "System") && all.All(p => !p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)), "snapshot: System is there and no name ends with .exe");
        int state = Sys.NotificationState();
        Check(state >= 1 && state <= 7, "full screen: Windows answered (state " + state + ", full screen: " + Sys.FullScreen() + ")");
        Check(Sys.ForegroundPid() >= 0 && Sys.Largest(all, 3).Contains(" GB"), "snapshot: the program in front and the largest programs can be named");

        // Rates between two snapshots: 2 s of processor time and 30 MB of I/O in 10 s, on this many processors.
        var then = new List<ProcInfo> { new ProcInfo { Pid = 9, Created = 1, Name = "worker", CpuTicks = 0, ReadBytes = 0 }, new ProcInfo { Pid = 10, Created = 1, Name = "old" } };
        var now = new List<ProcInfo> { new ProcInfo { Pid = 9, Created = 1, Name = "worker", CpuTicks = 20000000, ReadBytes = 10 * 1048576, WriteBytes = 20 * 1048576 }, new ProcInfo { Pid = 10, Created = 2, Name = "new", CpuTicks = 99999999 } };
        Sys.Rates(then, now, 10);
        Check(Math.Abs(now[0].CpuPercent - 20.0 / Environment.ProcessorCount) < 0.001 && Math.Abs(now[0].IoBytesPerSecond - 3 * 1048576) < 1, "rates: processor share and I/O speed come from two snapshots");
        Check(now[1].CpuPercent == 0, "rates: a new process under an old number is not compared with the old one");
        Check(Sys.Top(now, p => p.IoBytesPerSecond / 1048576, 3, v => v.ToString("0") + " MB/s", 0.5) == "worker 3 MB/s", "rates: the busiest programs are named");

        // Listening ports: open one and find it under this process.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            List<int> mine;
            Check(Sys.ListeningPorts().TryGetValue(Process.GetCurrentProcess().Id, out mine) && mine.Contains(port), "ports: a port this process listens on is found under it (" + port + ")");
        }
        finally { listener.Stop(); }

        DateTime boot = Sys.BootTime();
        Check(boot > new DateTime(2020, 1, 1) && boot < DateTime.Now, "boot: Windows says when it started (" + boot.ToString("yyyy-MM-dd HH:mm") + ")");
        var modules = Sys.KernelModules();
        Check(modules.Count > 20 && modules.Any(m => m.EndsWith("ntoskrnl.exe", StringComparison.OrdinalIgnoreCase)), "modules: the loaded kernel modules are listed (" + modules.Count + ")");
        Check(Sys.HungPids() != null, "hung: programs that do not respond can be asked for");
    }

    static void Busy()
    {
        var a = new Activity();
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        Func<long, long, List<ProcInfo>> at = (worker, sleeper) => new List<ProcInfo> {
            new ProcInfo { Pid = 1001, Created = 5, CpuTicks = worker, Name = "worker" },
            new ProcInfo { Pid = 1002, Created = 6, CpuTicks = sleeper, Name = "sleeper" } };
        // 4 minutes, a sample every 10 s: the worker uses 2 s of processor time per sample, the sleeper 10 ms.
        List<ProcInfo> now = null;
        for (int i = 0; i <= 24; i++) { now = at(i * 20000000L, i * 100000L); a.Sample(now, t.AddSeconds(i * 10)); }
        var end = t.AddSeconds(240);
        Check(!a.Idle(now[0], TimeSpan.FromMinutes(3), end), "activity: a program that keeps working is not idle");
        Check(a.Idle(now[1], TimeSpan.FromMinutes(3), end), "activity: a program that used 0.1 % of a core for 4 min is idle");
        Check(!a.Idle(now[1], TimeSpan.FromMinutes(5), end), "activity: but not idle for 5 min, it was first seen 4 min ago");
        var fresh = new ProcInfo { Pid = 1003, Created = 9, CpuTicks = 0, Name = "fresh" };
        Check(!a.Idle(fresh, TimeSpan.FromMinutes(3), end), "activity: an unknown process is not idle");
        now.Add(fresh);
        a.Sample(now, end.AddSeconds(10));
        Check(!a.Idle(fresh, TimeSpan.FromMinutes(3), end.AddSeconds(20)), "activity: a process that has just started is not idle");
        var reused = new ProcInfo { Pid = 1002, Created = 99, CpuTicks = 0, Name = "newcomer" };
        a.Sample(new List<ProcInfo> { now[0], reused }, end.AddSeconds(20));
        Check(!a.Idle(reused, TimeSpan.FromMinutes(3), end.AddSeconds(30)), "activity: a new process under an old number starts as busy");
        Check(!a.Idle(fresh, TimeSpan.Zero, end.AddSeconds(30)), "activity: a process that has gone is forgotten");
    }

    static void Trim()
    {
        // Two throwaway processes: one is emptied, the other is on the leave-alone list.
        Func<Process> ping = () => Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false });
        var first = ping();
        var second = ping();
        try
        {
            Thread.Sleep(1500);
            var mine = Sys.Processes().Where(p => p.Pid == first.Id || p.Pid == second.Id).ToList();
            Check(mine.Count == 2 && mine.All(p => p.Name.Equals("ping", StringComparison.OrdinalIgnoreCase)), "trim: both throwaway processes are in the snapshot");
            long before = mine.Where(p => p.Pid == first.Id).Select(p => p.WorkingSet).FirstOrDefault();
            var options = new CleanOptions();
            options.SkipPids.Add(second.Id);
            int left;
            int done = Cleaner.TrimProcesses(mine, options, out left);
            var now = Sys.Processes();
            var emptied = now.FirstOrDefault(p => p.Pid == first.Id);
            var kept = now.FirstOrDefault(p => p.Pid == second.Id);
            Check(done == 1 && left == 1, "trim: one was emptied and one left alone (" + done + " and " + left + ")");
            Check(emptied != null && emptied.WorkingSet < before * 0.7, "trim: the emptied one's working set fell (" + before / 1024 + " -> " + (emptied == null ? -1 : emptied.WorkingSet / 1024) + " KB)");
            Check(kept != null && kept.WorkingSet > before * 0.7, "trim: the other one kept its working set (" + (kept == null ? -1 : kept.WorkingSet / 1024) + " KB)");
            Check(Cleaner.TrimProcesses(mine, new CleanOptions(new[] { "ping" }), out left) == 0 && left == 2, "trim: a name on the never-clean list leaves every process of that program alone");
            Check(Cleaner.TrimProcesses(mine, null, out left) == 2 && left == 0, "trim: without options both are emptied");
            // The whole cleaning path, with every process but the first throwaway one left alone.
            var others = new CleanOptions();
            foreach (var p in Sys.Processes()) if (p.Pid != first.Id) others.SkipPids.Add(p.Pid);
            var result = Cleaner.Clean(Area.WorkingSets, others);
            Check(result.Errors.Count == 0 && result.Done.Count == 1 && result.Done[0].Contains("left alone"), "clean: the whole path leaves the listed processes alone (" + string.Join(", ", result.Done) + ")");
        }
        finally
        {
            foreach (var p in new[] { first, second }) { try { p.Kill(); } catch { } p.Dispose(); }
        }
    }

    static void Commit()
    {
        Func<string, double[]> big = root => new[] { 424.0, 168.0 }, tight = root => new[] { 424.0, 5.0 }, huge = root => new[] { 2000.0, 900.0 }, none = root => null;
        Func<double, double, bool> near = (a, b) => Math.Abs(a - b) < 0.01;
        Check(near(PageFile.MaxGb(32, 19, new[] { @"?:\pagefile.sys" }, big), 53), "page file: managed by Windows, it may grow to an eighth of the volume (53 of 424 GB)");
        Check(near(PageFile.MaxGb(8, 2, new[] { @"C:\pagefile.sys 0 0" }, huge), 24), "page file: or to 3 x RAM on a large volume (24 GB)");
        Check(near(PageFile.MaxGb(1, 1, new[] { @"C:\pagefile.sys 0 0" }, huge), 4), "page file: at least to 4 GB");
        Check(near(PageFile.MaxGb(32, 19, new[] { @"?:\pagefile.sys" }, tight), 23), "page file: free disk space limits the growth (19 + 5 - 1 = 23 GB)");
        Check(near(PageFile.MaxGb(32, 8, new[] { @"C:\pagefile.sys 8192 8192" }, big), 8), "page file: a fixed size cannot grow");
        Check(near(PageFile.MaxGb(32, 4, new[] { @"c:\pagefile.sys 4096 16384" }, big), 16), "page file: a custom one grows to its maximum");
        Check(near(PageFile.MaxGb(32, 0, new string[0], big), 0) && near(PageFile.MaxGb(32, 3, null, big), 3), "page file: with no entry the present size is final");
        Check(near(PageFile.MaxGb(32, 19, new[] { @"?:\pagefile.sys" }, none), 96), "page file: an unreadable volume leaves only the 3 x RAM rule");
        var c = PageFile.Read(Native.Read());
        Check(c.CeilingGb >= c.LimitGb && c.LimitGb > 0 && c.Percent > 0 && c.Percent <= 100,
            string.Format("commit on this PC: {0:0.0} of {1:0.0} GB, ceiling {2:0.0} GB ({3}%), can grow: {4}", c.CommitGb, c.LimitGb, c.CeilingGb, c.Percent, c.CanGrow));
    }

    static void SharedObjects()
    {
        string suffix = "SmartTest" + Process.GetCurrentProcess().Id;
        bool created;
        using (var once = Shared.OpenMutex("Local\\MemControl" + suffix, out created))
        using (var show = Shared.OpenEvent("Local\\MemControlShow" + suffix))
        {
            Check(created, "shared: the first copy creates the mutex and holds it");
            using (var child = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "/child " + suffix) { UseShellExecute = false, CreateNoWindow = true }))
            {
                bool exited = child.WaitForExit(15000);
                Check(exited && child.ExitCode == 0, "shared: a second copy finds the mutex taken and signals the first (exit code " + (exited ? child.ExitCode.ToString() : "none") + ")");
            }
            Check(show.WaitOne(0), "shared: the first copy receives the signal");
            Check(!show.WaitOne(0), "shared: once");
        }
    }

    // The second copy: it must find the mutex already there, must not get it, and must be able to signal the event.
    static int Child(string suffix)
    {
        bool created;
        using (var once = Shared.OpenMutex("Local\\MemControl" + suffix, out created))
        using (var show = Shared.OpenEvent("Local\\MemControlShow" + suffix))
        {
            if (created) return 2;
            if (once.WaitOne(0)) return 3;
            show.Set();
            return 0;
        }
    }

    static void ScheduledTask()
    {
        string name = "MemControlSmartTest" + Process.GetCurrentProcess().Id;
        string dir = Path.Combine(Path.GetTempPath(), name);
        string marker = Path.Combine(dir, "ran.txt"), script = Path.Combine(dir, "mark.js");
        string wscript = Path.Combine(Environment.SystemDirectory, "wscript.exe");
        Directory.CreateDirectory(dir);
        try
        {
            // The task runs a windowless script that writes a file: proof that the task's program really started.
            File.WriteAllText(script, "new ActiveXObject('Scripting.FileSystemObject').CreateTextFile('" + marker.Replace("\\", "\\\\") + "', true).Close();");
            string why = AdminTask.Create(name, wscript, "//B //Nologo \"" + script + "\"", AdminTask.UserSid(), false);
            Check(why == null, "task: Windows accepts the task definition" + (why == null ? "" : " (" + why + ")"));
            Check(AdminTask.State(name, wscript, wscript, dir) == AdminTask.Status.Ready, "task: it is found, enabled and points at the expected program");
            Check(AdminTask.State(name, @"C:\elsewhere\MemControl.exe", wscript, dir) == AdminTask.Status.OtherCopy, "task: a task that starts another file is told apart");
            Check(AdminTask.Run(name), "task: it starts on demand");
            for (int i = 0; i < 75 && !File.Exists(marker); i++) Thread.Sleep(200);
            Check(File.Exists(marker), "task: its program really ran");
        }
        finally
        {
            Check(AdminTask.Delete(name) == null, "task: it is removed");
            Check(AdminTask.State(name, wscript, wscript, dir) == AdminTask.Status.Missing, "task: and is gone");
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // The copy that runs as administrator, with a temporary folder standing in for Program Files and least privilege
    // standing in for administrator rights: when is the copy "ready", when "outdated"?
    static void ProtectedCopy()
    {
        string name = "MemControlCopyTest" + Process.GetCurrentProcess().Id;
        string dir = Path.Combine(Path.GetTempPath(), name), home = Path.Combine(dir, "home"), source = Path.Combine(home, "MemControl.exe"), copy = Path.Combine(dir, "protected", "MemControl.exe");
        Directory.CreateDirectory(home);
        try
        {
            File.Copy(Application.ExecutablePath, source, true);
            string why = AdminTask.Install(name, source, copy, home, AdminTask.UserSid(), " /instance CopyTest", false);
            Check(why == null && File.Exists(copy), "copy: the exe is copied to its place and the task created" + (why == null ? "" : " (" + why + ")"));
            Check(AdminTask.SameContent(source, copy), "copy: the copy has the same content");
            Check(AdminTask.State(name, copy, source, home) == AdminTask.Status.Ready, "copy: set up from this folder with the same exe, it is ready");
            Check(AdminTask.State(name, copy, source, home + "\\") == AdminTask.Status.Ready, "copy: a trailing backslash on the folder changes nothing");
            Check(AdminTask.State(name, copy, copy, @"C:\anywhere") == AdminTask.Status.Ready, "copy: asked by the copy itself, it is ready");
            Check(AdminTask.State(name, copy, source, Path.Combine(dir, "elsewhere")) == AdminTask.Status.Outdated, "copy: set up from another folder, it is outdated");
            File.AppendAllText(source, "x");
            Check(AdminTask.State(name, copy, source, home) == AdminTask.Status.Outdated, "copy: after the exe was updated, it is outdated");
            Check(AdminTask.Install(name, source, copy, home, AdminTask.UserSid(), " /instance CopyTest", false) == null && AdminTask.State(name, copy, source, home) == AdminTask.Status.Ready, "copy: setting up again renews it");
            Check(AdminTask.Arguments(@"C:\My Folder\", " /instance X") == "/takeover /home \"C:\\My Folder\" /instance X", "copy: the folder is quoted without the trailing backslash");
            Check(AdminTask.Uninstall(name, copy) == null && !File.Exists(copy) && !Directory.Exists(Path.GetDirectoryName(copy)), "copy: removing takes the task, the copy and its folder away");
            Check(AdminTask.State(name, copy, source, home) == AdminTask.Status.Missing, "copy: then nothing is left");
        }
        finally
        {
            AdminTask.Delete(name);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // The token an administrator copy uses for its files: the user without the administrators group, at medium integrity.
    static void PlainUser()
    {
        IntPtr token = AsUser.PlainToken();
        Check(token != IntPtr.Zero, "plain user: the restricted token can be made");
        if (token == IntPtr.Zero) return;
        string file = Path.Combine(Path.GetTempPath(), "memcontrol-plain-" + Process.GetCurrentProcess().Id + ".txt"), who = "";
        bool admin = true;
        using (WindowsIdentity.Impersonate(token))
        {
            File.WriteAllText(file, "written as the plain user");
            using (var id = WindowsIdentity.GetCurrent()) { who = id.Name; admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
        }
        using (var me = WindowsIdentity.GetCurrent()) Check(who == me.Name && !admin, "plain user: under it the thread is the same user, without administrator rights");
        Check(File.Exists(file) && File.ReadAllText(file) == "written as the plain user", "plain user: files in the user's own folders can be written under it");
        try { File.Delete(file); } catch { }
        bool ran = false;
        AsUser.Run(() => ran = true, true);
        Check(ran, "plain user: without administrator rights the operation simply runs");
    }

    // The figures behind "Why is it slow?": none of these may fail, whatever this PC reports.
    static void Figures()
    {
        using (var c = new Counters(Health.Paths))
        {
            c.Collect();
            Thread.Sleep(1200);
            c.Collect();
            double busy = c.One(@"\Processor Information(_Total)\% Processor Utility");
            if (double.IsNaN(busy)) busy = c.One(@"\Processor Information(_Total)\% Processor Time");
            Check(!double.IsNaN(busy) && busy >= 0 && busy <= 400, "counters: processor load can be read by its English name (" + busy.ToString("0") + "%)");
            Check(c.Many(@"\Thermal Zone Information(*)\Temperature") != null && c.Many(@"\No Such Counter(*)\Nothing").Count == 0 && double.IsNaN(c.One(@"\No Such Counter\Nothing")), "counters: a counter this PC lacks gives nothing, not an error");
            Console.WriteLine("INFO thermal zones: " + c.Many(@"\Thermal Zone Information(*)\Temperature").Count + ", graphics adapters with memory figures: " + c.Many(@"\GPU Adapter Memory(*)\Dedicated Usage").Count);
        }
        var gpus = Gpu.Adapters();
        Check(gpus != null, "graphics: adapters can be asked for");
        Console.WriteLine("INFO graphics adapters: " + string.Join("; ", gpus.Select(g => (g.DedicatedBytes / 1048576) + " MB" + (double.IsNaN(g.TemperatureC) ? ", no temperature" : ", " + g.TemperatureC.ToString("0") + " C"))));
        var power = PowerInfo.Read();
        Check(power != null && power.MaxProcessorState >= 1 && power.MaxProcessorState <= 100, "power: the plan and the processor cap can be read (cap " + power.MaxProcessorState + "%, mode \"" + power.Mode + "\")");
        Check(Health.StartupNames() != null, "startup: the programs that start with Windows can be listed");

        var rows = new List<HealthRow> { new HealthRow { Level = 0, Title = "Processor", Value = "12% busy" }, new HealthRow { Level = 1, Title = "Disk space", Value = "D: 9%" }, new HealthRow { Level = 2, Title = "Memory", Value = "RAM 97%", Advice = "Close something." } };
        Check(Health.Verdict(rows) == "Slowing this PC now: memory, disk space.", "health: the verdict names the worst lines first (" + Health.Verdict(rows) + ")");
        Check(Health.Verdict(rows.Take(1).ToList()) == "Nothing here is slowing this PC right now.", "health: with nothing wrong it says so");
        Check(Health.Report(rows).Contains("[!!] Memory: RAM 97%") && Health.Report(rows).Contains("Close something."), "health: the report can be copied as text");
    }

    static void History()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MemControlHistoryTest" + Process.GetCurrentProcess().Id);
        Directory.CreateDirectory(dir);
        string saved = Program.Home;
        Program.Home = dir + "\\";
        try
        {
            var t = new DateTime(2026, 3, 1, 8, 0, 0);
            var p = new HistoryPoint { At = t, Load = 71, CommitGb = 23.45, KernelGb = 2.125, Files = 123456, BootMinute = 987654 };
            var back = HistoryFile.Parse(HistoryFile.Line(p));
            Check(back != null && back.At == t && back.Load == 71 && Math.Abs(back.CommitGb - 23.45) < 0.001 && Math.Abs(back.KernelGb - 2.125) < 0.0001 && back.Files == 123456 && back.BootMinute == 987654, "history: a line reads back as it was written (" + HistoryFile.Line(p) + ")");
            Check(HistoryFile.Parse("nonsense") == null && HistoryFile.Parse("") == null && HistoryFile.Parse(null) == null, "history: a broken line is skipped");
            for (int i = 0; i < 40; i++) HistoryFile.Append(new HistoryPoint { At = t.AddMinutes(5 * i), Load = 50, CommitGb = 20, KernelGb = 2 + i * 0.01, Files = 1000 + i, BootMinute = 5 });
            Check(HistoryFile.Read(DateTime.MinValue).Count == 40 && HistoryFile.Read(t.AddMinutes(100)).Count == 20, "history: points are appended to the file and read back from a given time");

            // Kernel memory growing 1.2 GB a day for 2 days, a point every 5 minutes.
            var session = Enumerable.Range(0, 577).Select(i => new HistoryPoint { At = t.AddMinutes(5 * i), KernelGb = 2 + 1.2 * i / 288.0 }).ToList();
            double rate = Forecast.KernelGbPerDay(session);
            Check(Math.Abs(rate - 1.2) < 0.02, "forecast: the growth of kernel memory per day is found (" + rate.ToString("0.00") + " GB)");
            Check(double.IsNaN(Forecast.KernelGbPerDay(session.Take(60).ToList())), "forecast: under 6 hours of history there is none");
            string text = Forecast.Text(1.2, 2.8, 4, 8);
            Check(text.Contains("1.2 GB a day") || text.Contains("1,2 GB a day"), "forecast: the text gives the speed (" + text + ")");
            Check(text.Contains("yellow in about 1 day,") && text.Contains("red in about 4 days."), "forecast: and when the warning levels are reached");
            Check(Forecast.Text(0.1, 2, 4, 8) == "" && Forecast.Text(double.NaN, 2, 4, 8) == "", "forecast: slow or unknown growth gives no forecast");
        }
        finally
        {
            Program.Home = saved;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static void Leaks()
    {
        double first, last;
        Func<int, Func<int, double>, double[]> series = (n, f) => Enumerable.Range(0, n).Select(f).ToArray();
        Check(Growth.Steady(series(60, i => 100 + i * 5.0), 60, out first, out last) && last > first, "growth: a value that climbs all the time is steady growth");
        Check(!Growth.Steady(series(60, i => 500.0), 60, out first, out last), "growth: a flat value is not");
        Check(!Growth.Steady(series(60, i => 100 + (i % 20) * 30.0), 60, out first, out last), "growth: a sawtooth (grows, then gives it back) is not");
        Check(!Growth.Steady(series(60, i => i < 40 ? 100 + i * 5.0 : 150.0), 60, out first, out last), "growth: a value that fell back is not");
        Check(Growth.Steady(series(60, i => 100 + i * 5.0 + (i % 2) * 3), 60, out first, out last), "growth: small ripples do not hide a steady climb");
        Check(!Growth.Steady(series(20, i => 100 + i * 5.0), 20, out first, out last), "growth: under 30 samples nothing is judged");

        // An hour of samples: one process leaks 10 MB a minute, one leaks 100 handles a minute, one stays as it is.
        var g = new Growth();
        for (int i = 0; i < 60; i++)
            g.Sample(new List<ProcInfo> {
                new ProcInfo { Pid = 100, Created = 1, Name = "leaky", PrivateBytes = (200 + i * 10) * 1048576L, Handles = 500, Threads = 20 },
                new ProcInfo { Pid = 200, Created = 1, Name = "handles", PrivateBytes = 300 * 1048576L, Handles = 1000 + i * 100, Threads = 20 },
                new ProcInfo { Pid = 300, Created = 1, Name = "steady", PrivateBytes = 900 * 1048576L, Handles = 800, Threads = 30 } });
        var growing = g.Growing(1);
        Check(growing.Count == 2 && growing.Any(r => r.Name == "leaky" && r.What == "memory") && growing.Any(r => r.Name == "handles" && r.What == "handles"), "growth: the leaking programs are found and the steady one is not (" + string.Join("; ", growing.Select(r => r.Name + " " + r.What + " " + r.Growth + " " + r.PerHour)) + ")");
        Check(g.Longest == 60, "growth: an hour of samples is kept");

        var tags = KernelTags.Read();
        Check(tags.Count > 100 && tags.Any(t => t.Name == "File" && t.Live > 0), "kernel: Windows lists its memory by tag (" + tags.Count + " tags)");
        Check(KernelTags.Known("File") != null && KernelTags.Known("Ntff") != null && KernelTags.Known("Zzzz") == null, "kernel: the common tags of Windows are named, others are looked up");
        var watch = new KernelTags();
        watch.Sample(DateTime.Now);
        var rows = watch.Rows(14);
        Check(rows.Count >= 14 && rows[0].Bytes >= rows[13].Bytes && rows.All(r => r.Owner != ""), "kernel: the largest tags are listed, largest first");
        var unknown = rows.Where(r => KernelTags.Known(r.Tag) == null).Select(r => r.Tag).ToList();
        if (unknown.Count > 0)
        {
            using (var done = new ManualResetEvent(false))
            {
                var timer = Stopwatch.StartNew();
                watch.FindOwners(unknown, () => done.Set());
                bool finished = done.WaitOne(120000);
                var named = watch.Rows(14).Where(r => unknown.Contains(r.Tag)).ToList();
                Check(finished && named.All(r => r.Owner != "looking…"), "kernel: the driver files were searched for the other tags in " + timer.Elapsed.TotalSeconds.ToString("0.0") + " s");
                Console.WriteLine("INFO owners found: " + string.Join("; ", named.Take(6).Select(r => r.Tag + " = " + r.Owner)));
            }
        }
    }

    // "/task create NAME EXE ARGUMENTS" (least privilege) and "/task delete NAME".
    static int TaskCommand(string[] a)
    {
        string why = a[1] == "create" && a.Length >= 5 ? AdminTask.Create(a[2], a[3], a[4], AdminTask.UserSid(), false)
            : a[1] == "delete" ? AdminTask.Delete(a[2])
            : "usage: /task create NAME EXE ARGUMENTS | /task delete NAME";
        Console.WriteLine(why ?? "done");
        return why == null ? 0 : 1;
    }

    static string Elevation(int pid)
    {
        IntPtr h = Native.OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return "cannot open the process";
        try
        {
            IntPtr token;
            if (!Native.OpenProcessToken(h, 0x8, out token)) return "cannot read its token";
            try { int elevated, returned; return GetTokenInformation(token, 20, out elevated, 4, out returned) ? (elevated != 0 ? "elevated" : "not elevated") : "unknown"; }
            finally { Native.CloseHandle(token); }
        }
        finally { Native.CloseHandle(h); }
    }

    static bool CanWrite(string file)
    {
        try { using (File.Open(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) return true; }
        catch { return false; }
    }

    // "/admin-test MEMCONTROL.EXE": the real thing, with one UAC prompt. An elevated helper (this exe) copies MemControl
    // into Program Files\MemControlSelfTest and creates the task MemControlSelfTest; then, without the rights, a copy of
    // MemControl is started and must hand over to the administrator copy; at the end the helper removes everything.
    static int AdminTest(string memcontrol)
    {
        const string instance = "SelfTest";
        string id = Process.GetCurrentProcess().Id.ToString(), name = "MemControl" + instance;
        string home = Path.Combine(Path.GetTempPath(), name + id), source = Path.Combine(home, "MemControl.exe");
        string copy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), name, "MemControl.exe");
        Directory.CreateDirectory(home);
        File.Copy(memcontrol, source, true);
        File.WriteAllText(Path.Combine(home, "memcontrol.ini"), "areas=0\r\nautoOnPercent=0\r\nautoEvery=0\r\nnotify=0\r\nhotkey=0\r\ncommitWarn=0\r\nalwaysAdmin=1\r\n");
        using (var installed = Shared.OpenEvent("Local\\MemControlTestInstalled" + id))
        using (var cleanup = Shared.OpenEvent("Local\\MemControlTestCleanup" + id))
        {
            Process helper;
            try { helper = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "/admin-helper " + id + " \"" + source + "\" \"" + copy + "\" \"" + home + "\" " + AdminTask.UserSid()) { UseShellExecute = true, Verb = "runas" }); }
            catch (Win32Exception) { Console.WriteLine("SKIPPED: the administrator prompt was declined"); return 3; }
            try
            {
                if (!installed.WaitOne(90000)) { Console.WriteLine("FAIL the elevated helper did not report within 90 s"); return 1; }
                string report = File.Exists(Path.Combine(home, "helper.txt")) ? File.ReadAllText(Path.Combine(home, "helper.txt")) : "no report";
                Check(report == "installed", "admin: the copy went into Program Files and the task was created with highest privileges (" + report + ")");
                Check(AdminTask.State(name, copy, source, home) == AdminTask.Status.Ready, "admin: without the rights the task can be read and the copy matches");
                Check(!CanWrite(copy), "admin: a program without the rights cannot change the copy");

                var first = Process.Start(new ProcessStartInfo(source, "/instance " + instance) { UseShellExecute = false });
                bool left = first.WaitForExit(40000);
                Check(left && first.ExitCode == 0, "admin: the copy started without the rights stepped aside");
                Thread.Sleep(2000);
                var running = Sys.Processes().Where(p => p.Name == "MemControl" && string.Equals(ProcessTools.PathOf(p.Pid), copy, StringComparison.OrdinalIgnoreCase)).ToList();
                Check(running.Count == 1, "admin: the copy from Program Files is running (" + running.Count + ")");
                if (running.Count == 1) Check(Elevation(running[0].Pid) == "elevated", "admin: it has administrator rights, and no prompt appeared (" + Elevation(running[0].Pid) + ")");
                string log = Path.Combine(home, "memcontrol.log");
                Check(File.Exists(log) && File.ReadAllText(log).Contains("takeover"), "admin: it keeps its log in the user's folder, written as the plain user");

                using (var quit = Shared.OpenEvent("Local\\MemControlQuit" + instance)) quit.Set();
                Thread.Sleep(3000);
                Check(!Sys.Processes().Any(p => running.Any(r => r.Pid == p.Pid && r.Created == p.Created)), "admin: asked to close, the administrator copy closed");

                // Now the administrator copy starts first, so the shared objects are its own. Can a program without the rights still reach it?
                Check(AdminTask.Run(name), "admin: without the rights the task can be started");
                Thread.Sleep(4000);
                var again = Sys.Processes().Where(p => p.Name == "MemControl" && string.Equals(ProcessTools.PathOf(p.Pid), copy, StringComparison.OrdinalIgnoreCase)).ToList();
                Check(again.Count == 1 && Elevation(again[0].Pid) == "elevated", "admin: the task alone starts the administrator copy (" + again.Count + " running)");
                var second = Process.Start(new ProcessStartInfo(source, "/instance " + instance) { UseShellExecute = false });
                bool ended = second.WaitForExit(20000);
                Check(ended && second.ExitCode == 0, "admin: a start without the rights finds the administrator copy and ends at once (exit code " + (ended ? second.ExitCode.ToString() : "none") + ")");
                Check(Sys.Processes().Any(p => again.Any(r => r.Pid == p.Pid && r.Created == p.Created)), "admin: the administrator copy keeps running");
                using (var quit = Shared.OpenEvent("Local\\MemControlQuit" + instance)) quit.Set();
                Thread.Sleep(3000);
                Check(!Sys.Processes().Any(p => again.Any(r => r.Pid == p.Pid && r.Created == p.Created)), "admin: a signal from a program without the rights reaches an administrator copy that started first");
            }
            finally
            {
                cleanup.Set();
                helper.WaitForExit(40000);
            }
            Check(AdminTask.State(name, copy, source, home) == AdminTask.Status.Missing, "admin: the task is removed");
            Check(!File.Exists(copy), "admin: the copy is removed from Program Files");
        }
        try { Directory.Delete(home, true); } catch { }
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }

    // The elevated half of "/admin-test": set up, say so, wait until the checks are done, remove everything.
    static int AdminHelper(string[] a)
    {
        string id = a[1], source = a[2], copy = a[3], home = a[4], sid = a[5];
        const string name = "MemControlSelfTest";
        using (var installed = Shared.OpenEvent("Local\\MemControlTestInstalled" + id))
        using (var cleanup = Shared.OpenEvent("Local\\MemControlTestCleanup" + id))
        {
            string why = Native.IsAdmin() ? AdminTask.Install(name, source, copy, home, sid, " /instance SelfTest") : "the helper has no administrator rights";
            try { File.WriteAllText(Path.Combine(home, "helper.txt"), why ?? "installed"); } catch { }
            installed.Set();
            cleanup.WaitOne(300000);
            foreach (var p in Process.GetProcessesByName("MemControl"))
            {
                try { if (string.Equals(ProcessTools.PathOf(p.Id), copy, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); } } catch { }
                p.Dispose();
            }
            AdminTask.Uninstall(name, copy);
            return why == null ? 0 : 1;
        }
    }

    // A tray of its own for the modes below: a separate instance name, and its settings, log and history in the given
    // folder, with automatic cleaning, the hotkey and notifications off.
    static Tray TestTray(string folder, string instance, string extraSettings, EventWaitHandle[] events)
    {
        Directory.CreateDirectory(folder);
        Program.Instance = instance;
        Program.Home = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        File.WriteAllText(Path.Combine(folder, "memcontrol.ini"), "autoOnPercent=0\r\nautoEvery=0\r\nnotify=0\r\nhotkey=0\r\ncommitWarn=0\r\n" + extraSettings);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        events[0] = Shared.OpenEvent("Local\\MemControlShow" + instance);
        events[1] = Shared.OpenEvent("Local\\MemControlProcesses" + instance);
        events[2] = Shared.OpenEvent("Local\\MemControlQuit" + instance);
        return new Tray(events[0], events[1], events[2], false);
    }

    // "/auto FOLDER": one automatic, gentle cleaning through the real path, in a tray that has only just started. Nothing
    // has been seen idle for 3 minutes yet, so every program is left alone: the path runs without touching your programs.
    static int Auto(string folder)
    {
        var events = new EventWaitHandle[3];
        var tray = TestTray(folder, "SmartTestAuto", "areas=1\r\ngentle=1\r\n", events);
        tray.CleanNow("test: automatic and gentle", true, "a note for the log");
        var until = DateTime.UtcNow.AddSeconds(20);
        while (tray.Cleaning && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(50); }
        string line = tray.Recent.Count > 0 ? tray.Recent[0] : "no result";
        Console.WriteLine(line);
        Console.WriteLine("status: \"" + tray.AutoStatus + "\", commit " + tray.Commit.Percent + "% of the ceiling, processes seen: " + tray.Processes.Count + ", history points: " + tray.LongHistory.Count);
        tray.Quit();
        foreach (var e in events) e.Dispose();
        return line.Contains("left alone") && line.Contains("a note for the log") ? 0 : 1;
    }

    class OffMain : MainWindow
    {
        public OffMain(Tray tray) : base(tray) { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); ShowInTaskbar = false; }
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    class OffHealth : HealthWindow
    {
        public OffHealth(Tray tray) : base(tray) { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); ShowInTaskbar = false; }
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    class OffLeaks : LeakWindow
    {
        public OffLeaks(Tray tray) : base(tray) { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); ShowInTaskbar = false; }
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    class OffProcesses : ProcessWindow
    {
        public OffProcesses(Tray tray) : base(tray) { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); ShowInTaskbar = false; }
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    static void Picture(Form window, string file)
    {
        Application.DoEvents();
        using (var picture = new Bitmap(window.Width, window.Height))
        {
            window.DrawToBitmap(picture, new Rectangle(0, 0, window.Width, window.Height));
            picture.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(30); }
    }

    static List<HealthRow> DemoHealth()
    {
        Func<int, string, string, string, HealthRow> row = (level, title, value, advice) => new HealthRow { Level = level, Title = title, Value = value, Advice = advice, Action = () => { } };
        return new List<HealthRow> {
            row(0, "Processor", "38% busy", "Busiest: game 22%, browser 9%, editor 4%."),
            row(2, "Processor speed", "61% of its rated speed", "Windows is slowing the processor to 61% to cool it. Clear the vents, lift the back of the laptop, check the fan."),
            row(1, "Temperature", "system 96 °C · graphics 84 °C", "It is running hot. Hot chips slow themselves down: clear the vents and give the PC air."),
            row(1, "Graphics", "97% busy · memory 7.6 of 8.0 GB", "Graphics memory is almost full. Games stutter when it runs out: lower the texture quality or close what uses it. Largest: game 6.1 GB, browser 0.9 GB."),
            row(0, "Disk", "7% busy · 3.2 MB/s", "Most active: game 2.4 MB/s."),
            row(1, "Disk space", "C: 212 GB free · D: 38 GB free (8%)", "A drive is getting full. Leave at least a tenth of it free."),
            row(0, "Memory", "RAM 71% · free 1.2 GB · cache 6.4 GB · compressed 1.8 GB", "Windows is not short of RAM right now. The cache is given back the moment a program needs it."),
            row(0, "Reserved memory (commit)", "29.4 of 37.9 GB, can grow to 68", "There is room. Programs fail only when this reaches a limit that cannot rise."),
            row(1, "Windows kernel memory", "5.2 GB", "A driver is leaking kernel memory. Cleaning cannot free it: Restart (not Shut down) when convenient. It has grown about 1.4 GB a day since the last restart: red in about 2 days."),
            row(0, "Power", "plugged in · plan \"Balanced\" · balanced", "The power settings are not slowing the PC. A laptop maker's own quiet mode can still do so."),
            row(0, "Drivers", "0.8% of the processor", "Drivers are not getting in the way."),
            row(0, "Windows session", "running for 3.7 days", "Shut down does not end this session (Fast Startup is on); Restart does."),
            row(1, "Startup programs", "14", "Many programs start with Windows and keep running. Switch off the ones you do not need: cloud sync, chat, game launcher, updater, …."),
        };
    }

    // "/shots FOLDER [demo]": every window drawn off screen into FOLDER, without showing them or taking the keyboard focus.
    // With "demo" the health and leak windows and the top list show example figures instead of this PC's programs.
    static int Shots(string folder, bool demo)
    {
        var events = new EventWaitHandle[3];
        var tray = TestTray(folder, "SmartTestShots", "never=game, obs64\r\n", events);
        if (demo) { var s = tray.Settings; s.AutoOnPercent = true; s.AutoPercent = 90; s.Notify = true; s.Hotkey = true; s.CommitWarn = true; } // as a fresh install shows them; nothing is saved
        Pump(2500);
        using (var main = new OffMain(tray))
        {
            main.Show();
            main.Refresh(tray);
            if (demo)
            {
                var top = (ListView)typeof(MainWindow).GetField("top", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(main);
                top.Items.Clear();
                foreach (var r in new[] { "game|2|11,820", "browser|24|4,310", "editor|9|2,150", "chat|6|890", "virtual machine|1|4,096", "antivirus|1|640", "cloud sync|3|410", "music|5|380", "launcher|4|350", "mail|2|290", "terminal|7|240", "notes|1|120" })
                    top.Items.Add(new ListViewItem(r.Split('|')));
            }
            Picture(main, Path.Combine(folder, "main.png"));
        }
        using (var health = new OffHealth(tray))
        {
            health.Show();
            if (demo) health.SetRows(DemoHealth());
            else { health.Prepare(); Pump(2200); health.Prepare(); }
            Picture(health, Path.Combine(folder, "health.png"));
        }
        using (var leaks = new OffLeaks(tray))
        {
            leaks.Show();
            if (demo)
                leaks.SetRows(new List<GrowthRow> {
                        new GrowthRow { Name = "editor-helper", Pid = 4312, What = "memory", Growth = "+1.8 GB", PerHour = "0.6 GB/h", Now = "3.1 GB", Watched = "3.0 h" },
                        new GrowthRow { Name = "sync-agent", Pid = 9120, What = "handles", Growth = "+41,200", PerHour = "13,700/h", Now = "58,310", Watched = "3.0 h" } },
                    new List<TagRow> {
                        new TagRow { Tag = "File", Owner = KernelTags.Known("File"), Bytes = 1240L << 20, SinceStart = 1180L << 20, LastHour = 96L << 20, Live = 4120000 },
                        new TagRow { Tag = "FMfn", Owner = KernelTags.Known("FMfn"), Bytes = 980L << 20, SinceStart = 930L << 20, LastHour = 74L << 20, Live = 2080000 },
                        new TagRow { Tag = "MmSt", Owner = KernelTags.Known("MmSt"), Bytes = 210L << 20, SinceStart = 12L << 20, LastHour = 1L << 20, Live = 20400 },
                        new TagRow { Tag = "Gfx1", Owner = "displaydriver.sys (a graphics vendor)", Bytes = 164L << 20, SinceStart = 61L << 20, LastHour = 5L << 20, Live = 51200 },
                        new TagRow { Tag = "Ntff", Owner = KernelTags.Known("Ntff"), Bytes = 96L << 20, SinceStart = 4L << 20, LastHour = 0, Live = 68000 },
                        new TagRow { Tag = "EtwB", Owner = KernelTags.Known("EtwB"), Bytes = 70L << 20, SinceStart = 0, LastHour = 0, Live = 1210 },
                        new TagRow { Tag = "Toke", Owner = KernelTags.Known("Toke"), Bytes = 37L << 20, SinceStart = 2L << 20, LastHour = 0, Live = 17900 },
                        new TagRow { Tag = "Thre", Owner = KernelTags.Known("Thre"), Bytes = 23L << 20, SinceStart = 1L << 20, LastHour = 0, Live = 6900 } },
                    "Kernel memory: 5.2 GB (paged 3.9, nonpaged 1.3). It has grown about 1.4 GB a day since the last restart: red in about 2 days.\nEnded programs that something still holds on to: 96 processes and 640 threads. A few hundred is normal; many thousands are a leak in the program that holds them.", 180);
            else { leaks.Fill(); Pump(15000); leaks.Fill(); }
            Picture(leaks, Path.Combine(folder, "leaks.png"));
        }
        if (!demo)
            using (var processes = new OffProcesses(tray))
            {
                processes.Show();
                processes.Fill();
                Pump(1500);
                processes.Fill();
                Picture(processes, Path.Combine(folder, "processes.png"));
            }
        tray.Quit();
        foreach (var e in events) e.Dispose();
        return 0;
    }
}
