// MemControl's process manager: every process with its memory and CPU, a filter, sorting, and the actions
// End process, End process tree, End all with this name, Trim memory and Open file location.
// Windows' own critical processes are protected (ending them crashes or logs off Windows), and so is MemControl.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class ProcessTools
{
    // Ending any of these crashes Windows, logs you off or breaks the desktop; they are never ended here.
    public static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "Idle", "System", "Registry", "Memory Compression", "Secure System", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "lsaiso", "svchost", "fontdrvhost", "dwm", "LsaIso", "sihost", "ctfmon", "MsMpEng",
        "NisSrv", "SecurityHealthService", "audiodg", "spoolsv", "WUDFHost", "MemControl",
    };

    public static bool IsProtected(string name, int pid)
    {
        return pid <= 4 || pid == Process.GetCurrentProcess().Id || Protected.Contains(name);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public int Size, Usage, ProcessId; public IntPtr DefaultHeapId; public int ModuleId, Threads, ParentProcessId, PriClassBase, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(int flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    /// <summary>pid -> parent pid, from one snapshot.</summary>
    public static Dictionary<int, int> Parents()
    {
        var map = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new ProcessEntry { Size = Marshal.SizeOf(typeof(ProcessEntry)) };
            if (Process32FirstW(snap, ref e)) do { map[e.ProcessId] = e.ParentProcessId; } while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>The process and every descendant, children first.</summary>
    public static List<int> Tree(int root, Dictionary<int, int> parents)
    {
        var order = new List<int>();
        Action<int> walk = null;
        walk = pid =>
        {
            foreach (var kv in parents) if (kv.Value == pid && kv.Key != pid && !order.Contains(kv.Key)) walk(kv.Key);
            order.Add(pid);
        };
        walk(root);
        return order;
    }

    public static string PathOf(int pid)
    {
        IntPtr h = Native.OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Ends one process; null on success, else the reason in plain words.</summary>
    public static string End(int pid)
    {
        try
        {
            using (var p = Process.GetProcessById(pid))
            {
                if (IsProtected(p.ProcessName, pid)) return p.ProcessName + " is protected";
                p.Kill();
                p.WaitForExit(3000);
                return null;
            }
        }
        catch (ArgumentException) { return null; } // already gone
        catch (Win32Exception) { return "access denied (run MemControl as administrator)"; }
        catch (InvalidOperationException) { return null; }
        catch (Exception e) { return e.Message; }
    }

    public static bool Trim(int pid)
    {
        IntPtr h = Native.OpenProcess(0x1000 | 0x0100, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return Native.EmptyWorkingSet(h); } finally { Native.CloseHandle(h); }
    }
}

class ProcessWindow : Form
{
    readonly Tray tray;
    readonly TextBox filter = new TextBox();
    readonly CheckBox group = new CheckBox();
    readonly ListView list = new ListView();
    readonly Label status = new Label();
    readonly Button end = new Button(), endTree = new Button(), endAll = new Button(), trim = new Button(), locate = new Button();
    readonly Timer refresh = new Timer();
    readonly Dictionary<int, TimeSpan> lastCpu = new Dictionary<int, TimeSpan>();
    DateTime lastSample = DateTime.MinValue;
    int sortColumn = 3; bool sortDown = true;

    class Row { public string Name, Path; public int Pid, Count; public long Private, WorkingSet; public double Cpu; }

    public ProcessWindow(Tray tray)
    {
        this.tray = tray;
        Text = "MemControl — Processes";
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 620);
        MinimumSize = new Size(700, 400);

        var find = new Label { Text = "Filter:", AutoSize = true, Left = 12, Top = 15 };
        filter.SetBounds(60, 11, 260, 26);
        filter.TextChanged += (o, e) => Fill();
        group.SetBounds(336, 12, 200, 24); group.Text = "Group by program";
        group.CheckedChanged += (o, e) => { BuildColumns(); Fill(); };

        list.SetBounds(12, 44, 876, 500);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.View = View.Details; list.FullRowSelect = true; list.MultiSelect = true; list.HideSelection = false;
        list.ColumnClick += (o, e) => { if (sortColumn == e.Column) sortDown = !sortDown; else { sortColumn = e.Column; sortDown = e.Column >= 2; } Fill(); };
        list.SelectedIndexChanged += (o, e) => UpdateButtons();
        list.KeyDown += (o, e) => { if (e.KeyCode == Keys.Delete) EndSelected(false); };
        BuildColumns();

        int y = 556;
        Button(end, "End process", 12, y, (o, e) => EndSelected(false));
        Button(endTree, "End process tree", 140, y, (o, e) => EndSelected(true));
        Button(endAll, "End all with this name", 290, y, (o, e) => EndByName());
        Button(trim, "Trim memory", 470, y, (o, e) => TrimSelected());
        Button(locate, "Open file location", 590, y, (o, e) => Locate());
        foreach (var b in new[] { end, endTree, endAll, trim, locate }) b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        end.ForeColor = Color.FromArgb(190, 30, 45);
        status.SetBounds(12, 592, 876, 22); status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; status.ForeColor = Color.DimGray;

        var menu = new ContextMenuStrip();
        menu.Items.Add("End process", null, (o, e) => EndSelected(false));
        menu.Items.Add("End process tree", null, (o, e) => EndSelected(true));
        menu.Items.Add("End all with this name", null, (o, e) => EndByName());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Trim memory", null, (o, e) => TrimSelected());
        menu.Items.Add("Open file location", null, (o, e) => Locate());
        menu.Items.Add("Copy path", null, (o, e) => { var r = Selected().FirstOrDefault(); if (r != null && r.Path != "") Clipboard.SetText(r.Path); });
        menu.Items.Add(new ToolStripSeparator());
        // The same list as "Never clean" in the main window: cleaning leaves every process of this program alone.
        var neverClean = new ToolStripMenuItem("Never clean this program");
        neverClean.Click += (o, e) =>
        {
            var r = Selected().FirstOrDefault();
            if (r == null) return;
            if (!tray.Settings.Exclude.Remove(r.Name)) tray.Settings.Exclude.Add(r.Name);
            tray.Settings.Save();
            status.Text = tray.Settings.Exclude.Contains(r.Name) ? r.Name + " will never be cleaned." : r.Name + " can be cleaned again.";
        };
        menu.Items.Add(neverClean);
        menu.Opening += (o, e) => { var r = Selected().FirstOrDefault(); neverClean.Enabled = r != null; neverClean.Checked = r != null && tray.Settings.Exclude.Contains(r.Name); };
        list.ContextMenuStrip = menu;

        Controls.AddRange(new Control[] { find, filter, group, list, end, endTree, endAll, trim, locate, status });
        refresh.Interval = 3000;
        refresh.Tick += (o, e) => { if (Visible) Fill(); };
        FormClosing += (o, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); refresh.Stop(); } };
        UpdateButtons();
    }

    void Button(Button b, string text, int x, int y, EventHandler click)
    {
        b.Text = text; b.SetBounds(x, y, b == endAll ? 170 : b == locate ? 150 : b == endTree ? 140 : 118, 30); b.Click += click;
    }

    void BuildColumns()
    {
        list.Columns.Clear();
        list.Columns.Add("Program", 210);
        list.Columns.Add(group.Checked ? "Processes" : "PID", 80, HorizontalAlignment.Right);
        list.Columns.Add("CPU %", 70, HorizontalAlignment.Right);
        list.Columns.Add("Memory (MB)", 110, HorizontalAlignment.Right);
        list.Columns.Add("Working set (MB)", 120, HorizontalAlignment.Right);
        list.Columns.Add("Path", 260);
    }

    public void Open(string programFilter)
    {
        if (programFilter != null) { filter.Text = programFilter; group.Checked = false; }
        Fill();
        Show(); WindowState = FormWindowState.Normal; Activate();
        refresh.Start();
    }

    List<Row> Read()
    {
        var rows = new List<Row>();
        var now = DateTime.Now;
        double seconds = lastSample == DateTime.MinValue ? 0 : (now - lastSample).TotalSeconds;
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var r = new Row { Name = p.ProcessName, Pid = p.Id, Count = 1 };
                try { r.Private = p.PrivateMemorySize64; r.WorkingSet = p.WorkingSet64; } catch { }
                try
                {
                    var cpu = p.TotalProcessorTime; TimeSpan before;
                    if (seconds > 0 && lastCpu.TryGetValue(p.Id, out before)) r.Cpu = Math.Max(0, (cpu - before).TotalSeconds / seconds / Environment.ProcessorCount * 100);
                    lastCpu[p.Id] = cpu;
                }
                catch { }
                r.Path = ProcessTools.PathOf(p.Id);
                seen.Add(p.Id);
                rows.Add(r);
            }
            catch { }
            finally { p.Dispose(); }
        }
        foreach (var gone in lastCpu.Keys.Where(k => !seen.Contains(k)).ToList()) lastCpu.Remove(gone);
        lastSample = now;
        return rows;
    }

    List<Row> current = new List<Row>();

    void Fill()
    {
        var rows = Read();
        string f = filter.Text.Trim();
        if (f != "") rows = rows.Where(r => r.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || r.Path.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || r.Pid.ToString() == f).ToList();
        if (group.Checked)
            rows = rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(g => new Row {
                Name = g.Key, Pid = g.First().Pid, Count = g.Count(), Cpu = g.Sum(r => r.Cpu), Private = g.Sum(r => r.Private),
                WorkingSet = g.Sum(r => r.WorkingSet), Path = g.Select(r => r.Path).FirstOrDefault(p => p != "") ?? "" }).ToList();
        Func<Row, IComparable> key = r => sortColumn == 0 ? (IComparable)r.Name.ToLowerInvariant() : sortColumn == 1 ? (IComparable)(group.Checked ? r.Count : r.Pid)
            : sortColumn == 2 ? (IComparable)r.Cpu : sortColumn == 3 ? (IComparable)r.Private : sortColumn == 4 ? (IComparable)r.WorkingSet : r.Path.ToLowerInvariant();
        rows = (sortDown ? rows.OrderByDescending(key) : rows.OrderBy(key)).ToList();

        var keep = new HashSet<string>(list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag));
        int topIndex = list.TopItem != null ? list.TopItem.Index : 0;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var r in rows)
        {
            string tag = group.Checked ? "n:" + r.Name : "p:" + r.Pid;
            var item = new ListViewItem(new[] { r.Name, group.Checked ? r.Count.ToString() : r.Pid.ToString(), r.Cpu.ToString("0.0"),
                (r.Private / (1 << 20)).ToString("N0"), (r.WorkingSet / (1 << 20)).ToString("N0"), r.Path }) { Tag = tag };
            if (ProcessTools.IsProtected(r.Name, r.Pid)) item.ForeColor = Color.Gray;
            if (keep.Contains(tag)) item.Selected = true;
            list.Items.Add(item);
        }
        list.EndUpdate();
        if (topIndex > 0 && topIndex < list.Items.Count) list.TopItem = list.Items[topIndex];
        current = rows;
        long total = rows.Sum(r => r.Private);
        status.Text = string.Format("{0} {1} · {2:N0} MB private memory{3} · gray = protected Windows processes (cannot be ended)",
            rows.Sum(r => r.Count), rows.Sum(r => r.Count) == 1 ? "process" : "processes", total / (1 << 20), f != "" ? " (filtered)" : "");
        UpdateButtons();
    }

    List<Row> Selected()
    {
        var tags = new HashSet<string>(list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag));
        return current.Where(r => tags.Contains(group.Checked ? "n:" + r.Name : "p:" + r.Pid)).ToList();
    }

    void UpdateButtons()
    {
        var sel = Selected();
        bool any = sel.Count > 0, endable = sel.Any(r => !ProcessTools.IsProtected(r.Name, r.Pid));
        end.Enabled = endable; endTree.Enabled = endable && !group.Checked; endAll.Enabled = endable;
        trim.Enabled = any; locate.Enabled = sel.Count == 1 && sel[0].Path != "";
    }

    // The pids behind the selection (a grouped row is every process with that name).
    List<KeyValuePair<int, string>> Targets(bool tree)
    {
        var targets = new List<KeyValuePair<int, string>>();
        var all = Process.GetProcesses().Select(p => { var kv = new KeyValuePair<int, string>(p.Id, p.ProcessName); p.Dispose(); return kv; }).ToList();
        var names = all.ToDictionary(kv => kv.Key, kv => kv.Value);
        var parents = tree ? ProcessTools.Parents() : null;
        foreach (var r in Selected())
        {
            if (group.Checked) targets.AddRange(all.Where(kv => kv.Value.Equals(r.Name, StringComparison.OrdinalIgnoreCase)));
            else if (tree) targets.AddRange(ProcessTools.Tree(r.Pid, parents).Where(names.ContainsKey).Select(pid => new KeyValuePair<int, string>(pid, names[pid])));
            else targets.Add(new KeyValuePair<int, string>(r.Pid, r.Name));
        }
        return targets.GroupBy(t => t.Key).Select(g => g.First()).ToList();
    }

    void EndSelected(bool tree)
    {
        if (Selected().Count == 0) return;
        Confirm(Targets(tree), tree ? "End these processes and everything they started?" : "End these processes?");
    }

    void EndByName()
    {
        var names = new HashSet<string>(Selected().Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return;
        var targets = Process.GetProcesses().Where(p => names.Contains(p.ProcessName)).Select(p => { var kv = new KeyValuePair<int, string>(p.Id, p.ProcessName); p.Dispose(); return kv; }).ToList();
        Confirm(targets, "End every process named " + string.Join(", ", names) + "?");
    }

    void Confirm(List<KeyValuePair<int, string>> targets, string question)
    {
        var protectedOnes = targets.Where(t => ProcessTools.IsProtected(t.Value, t.Key)).ToList();
        var endable = targets.Where(t => !ProcessTools.IsProtected(t.Value, t.Key)).ToList();
        if (endable.Count == 0) { MessageBox.Show("These are protected Windows processes. Ending them would crash Windows or log you off, so MemControl does not.", "MemControl", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var lines = endable.GroupBy(t => t.Value, StringComparer.OrdinalIgnoreCase).Select(g => "  " + g.Key + (g.Count() > 1 ? " ×" + g.Count() : "") + "  (PID " + string.Join(", ", g.Take(6).Select(t => t.Key)) + (g.Count() > 6 ? ", …" : "") + ")");
        string text = question + "\n\n" + string.Join("\n", lines.Take(15)) + (lines.Count() > 15 ? "\n  …" : "")
            + "\n\nUnsaved work in these programs will be lost." + (protectedOnes.Count > 0 ? "\n" + protectedOnes.Count + " protected Windows process(es) in the selection will be left running." : "");
        if (MessageBox.Show(text, "MemControl — end processes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var failures = new List<string>();
        int ended = 0;
        foreach (var t in endable)
        {
            string why = ProcessTools.End(t.Key);
            if (why == null) ended++; else failures.Add(t.Value + " (" + t.Key + "): " + why);
        }
        Log.Write("ended " + ended + " process(es): " + string.Join(", ", endable.Select(t => t.Value + " " + t.Key).Take(20)) + (failures.Count > 0 ? "; failed: " + string.Join("; ", failures) : ""));
        status.Text = "Ended " + ended + " process(es)." + (failures.Count > 0 ? " Not ended: " + failures.Count + "." : "");
        if (failures.Count > 0) MessageBox.Show("Could not end:\n\n" + string.Join("\n", failures.Take(15)), "MemControl", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Fill();
    }

    void TrimSelected()
    {
        var targets = Targets(false);
        double before = Native.Read().AvailGb;
        int done = targets.Count(t => ProcessTools.Trim(t.Key));
        System.Threading.Thread.Sleep(300);
        double freed = Math.Max(0, Native.Read().AvailGb - before) * 1024;
        status.Text = string.Format("Trimmed {0} of {1} process(es); about {2:0} MB moved out of RAM.", done, targets.Count, freed);
        Log.Write(status.Text);
        Fill();
    }

    void Locate()
    {
        var r = Selected().FirstOrDefault();
        if (r == null || r.Path == "" || !File.Exists(r.Path)) return;
        Process.Start("explorer.exe", "/select,\"" + r.Path + "\"");
    }
}
