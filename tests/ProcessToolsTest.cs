// Checks MemControl's process actions on throwaway processes: protection, tree discovery, ending a tree, trimming.
// Build and run: tests\run-tests.cmd (exit code 0 = all passed).
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

static class ProcessToolsTest
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        // Protection: Windows' critical processes and MemControl itself are never ended.
        foreach (var name in new[] { "System", "csrss", "winlogon", "lsass", "svchost", "dwm", "services", "MemControl" })
            Check(ProcessTools.IsProtected(name, 1000), name + " is protected");
        Check(!ProcessTools.IsProtected("notepad", 1000), "an ordinary program is not protected");
        Check(ProcessTools.IsProtected("anything", 4), "PID 4 is protected");
        Check(ProcessTools.IsProtected("anything", Process.GetCurrentProcess().Id), "this process is protected");
        var csrss = Process.GetProcessesByName("csrss").FirstOrDefault();
        if (csrss != null) Check(ProcessTools.End(csrss.Id) != null && Process.GetProcesses().Any(p => p.Id == csrss.Id), "End refuses csrss and it keeps running");

        // A parent cmd that starts a child ping: the tree has both, children first; ending it ends both.
        var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false });
        Thread.Sleep(1500);
        var parents = ProcessTools.Parents();
        var tree = ProcessTools.Tree(parent.Id, parents);
        int child = tree.FirstOrDefault(p => { try { return Process.GetProcessById(p).ProcessName.Equals("PING", StringComparison.OrdinalIgnoreCase); } catch { return false; } });
        Check(tree.Count >= 2 && tree.Last() == parent.Id, "the tree holds the parent last and its child first (" + string.Join(",", tree) + ")");
        Check(child != 0 && Process.GetProcessById(child).ProcessName.Equals("PING", StringComparison.OrdinalIgnoreCase), "the child is ping");
        Check(ProcessTools.Trim(child), "Trim empties the child's working set");
        Check(ProcessTools.PathOf(child).EndsWith("PING.EXE", StringComparison.OrdinalIgnoreCase), "PathOf finds the child's file");
        foreach (var pid in tree) Check(ProcessTools.End(pid) == null, "End " + pid);
        Thread.Sleep(500);
        Check(tree.All(pid => { try { return Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return true; } }), "the whole tree is gone");
        Check(ProcessTools.End(parent.Id) == null, "ending a process that is already gone is not an error");

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
