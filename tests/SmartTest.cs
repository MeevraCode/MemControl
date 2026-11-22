// Checks MemControl's smart cleaning and the plumbing of "Always run as administrator" without touching your programs:
// the automatic-cleaning policy, the "Never clean" list, the process snapshot, activity tracking, leaving a process
// alone while trimming (on throwaway processes only), the commit ceiling, the named objects that two copies share, and
// a throwaway scheduled task with least privilege (the same definition as the real one, which needs administrator rights).
// Build and run: tests\run-tests.cmd (exit code 0 = all passed).
// "SmartTest.exe /window FILE.png" draws the main window off screen into a picture, to check the layout.
// "SmartTest.exe /task create NAME EXE ARGUMENTS" and "/task delete NAME" make and remove a least-privilege task.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

static class SmartTest
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "/child") return Child(args[1]);
        if (args.Length == 2 && args[0] == "/window") return Window(args[1]);
        if (args.Length >= 3 && args[0] == "/task") return TaskCommand(args);
        if (args.Length == 1 && args[0] == "/auto") return Auto();
        Console.WriteLine("INFO this account can use \"Always run as administrator\": " + AdminTask.Possible());
        Policy();
        Lists();
        Snapshot();
        Busy();
        Trim();
        Commit();
        SharedObjects();
        ScheduledTask();
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
        }
        Check(all.Any(p => p.Pid == 4 && p.Name == "System") && all.All(p => !p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)), "snapshot: System is there and no name ends with .exe");
        int state = Sys.NotificationState();
        Check(state >= 1 && state <= 7, "full screen: Windows answered (state " + state + ", full screen: " + Sys.FullScreen() + ")");
        Check(Sys.ForegroundPid() >= 0 && Sys.Largest(all, 3).Contains(" GB"), "snapshot: the program in front and the largest programs can be named");
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
            Check(AdminTask.State(name, wscript) == AdminTask.Status.Ready, "task: it is found, enabled and points at the expected program");
            Check(AdminTask.State(name, @"C:\elsewhere\MemControl.exe") == AdminTask.Status.OtherCopy, "task: a task for another copy is told apart");
            Check(AdminTask.Run(name), "task: it starts on demand");
            for (int i = 0; i < 75 && !File.Exists(marker); i++) Thread.Sleep(200);
            Check(File.Exists(marker), "task: its program really ran");
        }
        finally
        {
            Check(AdminTask.Delete(name) == null, "task: it is removed");
            Check(AdminTask.State(name, wscript) == AdminTask.Status.Missing, "task: and is gone");
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // "/task create NAME EXE ARGUMENTS" (least privilege) and "/task delete NAME": for trying the handover with a test copy.
    static int TaskCommand(string[] a)
    {
        string why = a[1] == "create" && a.Length >= 5 ? AdminTask.Create(a[2], a[3], a[4], AdminTask.UserSid(), false)
            : a[1] == "delete" ? AdminTask.Delete(a[2])
            : "usage: /task create NAME EXE ARGUMENTS | /task delete NAME";
        Console.WriteLine(why ?? "done");
        return why == null ? 0 : 1;
    }

    // "/auto": one automatic, gentle cleaning through the real path, in a tray that has only just started. Nothing has
    // been seen idle for 3 minutes yet, so every program is left alone: the path runs without touching your programs.
    static int Auto()
    {
        Program.Instance = "SmartTestAuto";
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using (var ask = Shared.OpenEvent("Local\\MemControlShow" + Program.Instance))
        using (var askProcesses = Shared.OpenEvent("Local\\MemControlProcesses" + Program.Instance))
        using (var quit = Shared.OpenEvent("Local\\MemControlQuit" + Program.Instance))
        {
            var tray = new Tray(ask, askProcesses, quit, false);
            tray.CleanNow("test: automatic and gentle", true, "a note for the log");
            var until = DateTime.UtcNow.AddSeconds(20);
            while (tray.Cleaning && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(50); }
            string line = tray.Recent.Count > 0 ? tray.Recent[0] : "no result";
            Console.WriteLine(line);
            Console.WriteLine("status: \"" + tray.AutoStatus + "\", commit " + tray.Commit.Percent + "% of the ceiling, processes seen: " + tray.Processes.Count);
            tray.Quit();
            return line.Contains("left alone") && line.Contains("a note for the log") ? 0 : 1;
        }
    }

    class Offscreen : MainWindow
    {
        public Offscreen(Tray tray) : base(tray) { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); ShowInTaskbar = false; }
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    // Draws the main window into a picture without showing it on screen or taking the keyboard focus.
    static int Window(string file)
    {
        Program.Instance = "SmartTestWindow";
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var ask = Shared.OpenEvent("Local\\MemControlShow" + Program.Instance))
        using (var askProcesses = Shared.OpenEvent("Local\\MemControlProcesses" + Program.Instance))
        using (var quit = Shared.OpenEvent("Local\\MemControlQuit" + Program.Instance))
        {
            var tray = new Tray(ask, askProcesses, quit, false);
            using (var window = new Offscreen(tray))
            {
                window.Show();
                window.Refresh(tray);
                Application.DoEvents();
                using (var picture = new Bitmap(window.Width, window.Height))
                {
                    window.DrawToBitmap(picture, new Rectangle(0, 0, window.Width, window.Height));
                    picture.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            tray.Quit();
        }
        return 0;
    }
}
