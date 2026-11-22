// Leak watch: programs that keep growing for hours, kernel memory by owner (pool tags, with the driver behind each),
// and a history on disk for the long graph and for the forecast of a kernel leak.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class HistoryPoint
{
    public DateTime At;
    public int Load;                 // RAM use, %
    public double CommitGb, KernelGb;
    public long Files, BootMinute;   // live file objects; the minute Windows started, which tells one session from the next
}

/// <summary>memcontrol-history.csv beside the settings: one line every 5 minutes, 30 days kept.</summary>
static class HistoryFile
{
    public const int KeepLines = 8640;
    static readonly CultureInfo inv = CultureInfo.InvariantCulture;
    static string File_ { get { return Path.Combine(Program.Folder, "memcontrol-history.csv"); } }

    public static string Line(HistoryPoint p)
    {
        return string.Join(",", p.At.ToString("yyyy-MM-ddTHH:mm:ss", inv), p.Load.ToString(inv), p.CommitGb.ToString("0.00", inv), p.KernelGb.ToString("0.000", inv), p.Files.ToString(inv), p.BootMinute.ToString(inv));
    }

    public static HistoryPoint Parse(string line)
    {
        var f = (line ?? "").Split(',');
        var p = new HistoryPoint();
        bool ok = f.Length >= 6 && DateTime.TryParseExact(f[0], "yyyy-MM-ddTHH:mm:ss", inv, DateTimeStyles.None, out p.At) && int.TryParse(f[1], NumberStyles.Integer, inv, out p.Load)
            && double.TryParse(f[2], NumberStyles.Float, inv, out p.CommitGb) && double.TryParse(f[3], NumberStyles.Float, inv, out p.KernelGb)
            && long.TryParse(f[4], NumberStyles.Integer, inv, out p.Files) && long.TryParse(f[5], NumberStyles.Integer, inv, out p.BootMinute);
        return ok ? p : null;
    }

    public static void Append(HistoryPoint p)
    {
        AsUser.Run(() => { try { File.AppendAllText(File_, Line(p) + "\r\n"); } catch { } }, true);
    }

    public static List<HistoryPoint> Read(DateTime since)
    {
        var points = new List<HistoryPoint>();
        AsUser.Run(() =>
        {
            try
            {
                if (!File.Exists(File_)) return;
                foreach (var line in File.ReadAllLines(File_))
                {
                    var p = Parse(line);
                    if (p != null && p.At >= since) points.Add(p);
                }
            }
            catch { }
        }, false);
        return points;
    }

    /// <summary>Cuts the file back to 30 days once it has grown well past that.</summary>
    public static void Trim()
    {
        AsUser.Run(() =>
        {
            try
            {
                if (!File.Exists(File_) || new FileInfo(File_).Length < 400 * 1024) return;
                var lines = File.ReadAllLines(File_);
                if (lines.Length > KeepLines + 600) File.WriteAllLines(File_, lines.Skip(lines.Length - KeepLines).ToArray());
            }
            catch { }
        }, true);
    }
}

/// <summary>How fast kernel memory grows in this Windows session, and when it reaches the warning levels.</summary>
static class Forecast
{
    static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? double.NaN : sorted[sorted.Count / 2];
    }

    /// <summary>GB a day, from the first and the last three points of the session; NaN with under 6 hours of history.</summary>
    public static double KernelGbPerDay(List<HistoryPoint> session)
    {
        if (session.Count < 12) return double.NaN;
        double days = (session[session.Count - 1].At - session[0].At).TotalDays;
        if (days < 0.25) return double.NaN;
        return (Median(session.Skip(session.Count - 3).Select(p => p.KernelGb)) - Median(session.Take(3).Select(p => p.KernelGb))) / days;
    }

    public static string Text(double perDay, double nowGb, double yellowGb, double redGb)
    {
        if (double.IsNaN(perDay) || perDay < 0.25) return "";
        Func<double, string> when = target =>
        {
            double days = (target - nowGb) / perDay;
            return days < 1 ? "within a day" : "in about " + days.ToString("0") + (days < 1.5 ? " day" : " days");
        };
        return "It has grown about " + perDay.ToString("0.0") + " GB a day since the last restart"
            + (nowGb < yellowGb ? ": yellow " + when(yellowGb) + ", red " + when(redGb) + "." : nowGb < redGb ? ": red " + when(redGb) + "." : ".");
    }
}

class GrowthRow
{
    public string Name = "", What = "", Growth = "", PerHour = "", Now = "", Watched = "";
    public int Pid;
    public double Score;
}

/// <summary>
/// Programs that keep growing. Each process is sampled once a minute for up to 4 hours: memory, handles and threads.
/// A value "climbs steadily" when the medians of five equal stretches of its history never fall back.
/// </summary>
class Growth
{
    public const int Capacity = 240;
    const double Mb = 1048576.0;

    class Series
    {
        public long Created;
        public string Name = "";
        public int Count;
        public readonly double[] Memory = new double[Capacity], Handles = new double[Capacity], Threads = new double[Capacity];
    }

    readonly Dictionary<int, Series> all = new Dictionary<int, Series>();

    /// <summary>How many samples the longest-watched process has.</summary>
    public int Longest { get { return all.Count == 0 ? 0 : all.Values.Max(s => s.Count); } }

    public void Sample(List<ProcInfo> processes)
    {
        var alive = new HashSet<int>();
        foreach (var p in processes)
        {
            if (p.Pid <= 4) continue;
            alive.Add(p.Pid);
            Series s;
            if (!all.TryGetValue(p.Pid, out s) || s.Created != p.Created) all[p.Pid] = s = new Series { Created = p.Created, Name = p.Name };
            if (s.Count == Capacity)
            {
                foreach (var values in new[] { s.Memory, s.Handles, s.Threads }) Array.Copy(values, 1, values, 0, Capacity - 1);
                s.Count--;
            }
            s.Memory[s.Count] = p.PrivateBytes;
            s.Handles[s.Count] = p.Handles;
            s.Threads[s.Count] = p.Threads;
            s.Count++;
        }
        foreach (var gone in all.Keys.Where(pid => !alive.Contains(pid)).ToList()) all.Remove(gone);
    }

    /// <summary>
    /// True when the first count values climb steadily: at least 30 of them, the medians of five equal stretches never
    /// fall back by more than a fiftieth of the whole rise, and at least three of the four steps go up.
    /// </summary>
    public static bool Steady(double[] values, int count, out double first, out double last)
    {
        first = last = 0;
        if (count < 30) return false;
        int size = count / 5;
        var medians = new double[5];
        for (int b = 0; b < 5; b++)
        {
            var part = new double[size];
            Array.Copy(values, count - (5 - b) * size, part, 0, size);
            Array.Sort(part);
            medians[b] = part[size / 2];
        }
        first = medians[0];
        last = medians[4];
        double rise = last - first;
        if (rise <= 0) return false;
        int ups = 0;
        for (int b = 1; b < 5; b++)
        {
            double step = medians[b] - medians[b - 1];
            if (step < -0.02 * rise) return false;
            if (step > 0) ups++;
        }
        return ups >= 3;
    }

    static string Size(double bytes) { return bytes >= 1024 * Mb ? (bytes / 1024 / Mb).ToString("0.0") + " GB" : (bytes / Mb).ToString("0") + " MB"; }

    /// <summary>The processes whose memory, handles or threads have climbed steadily and by a lot.</summary>
    public List<GrowthRow> Growing(double minutesPerSample)
    {
        var rows = new List<GrowthRow>();
        foreach (var kv in all)
        {
            var s = kv.Value;
            double first, last, hours = Math.Max(0.1, s.Count * minutesPerSample / 60.0 * 0.8); // from the middle of the first stretch to the middle of the last
            double minutes = s.Count * minutesPerSample;
            string watched = minutes >= 90 ? (minutes / 60).ToString("0.0") + " h" : minutes.ToString("0") + " min";
            if (Steady(s.Memory, s.Count, out first, out last) && (last - first >= 500 * Mb || (last - first >= 150 * Mb && last >= first * 1.2)))
                rows.Add(new GrowthRow { Name = s.Name, Pid = kv.Key, What = "memory", Growth = "+" + Size(last - first), PerHour = Size((last - first) / hours) + "/h", Now = Size(s.Memory[s.Count - 1]), Watched = watched, Score = (last - first) / Mb });
            if (Steady(s.Handles, s.Count, out first, out last) && last - first >= 2000 && last >= first * 1.3)
                rows.Add(new GrowthRow { Name = s.Name, Pid = kv.Key, What = "handles", Growth = "+" + (last - first).ToString("N0"), PerHour = ((last - first) / hours).ToString("N0") + "/h", Now = s.Handles[s.Count - 1].ToString("N0"), Watched = watched, Score = (last - first) / 10 });
            if (Steady(s.Threads, s.Count, out first, out last) && last - first >= 100 && last >= first * 1.3)
                rows.Add(new GrowthRow { Name = s.Name, Pid = kv.Key, What = "threads", Growth = "+" + (last - first).ToString("N0"), PerHour = ((last - first) / hours).ToString("N0") + "/h", Now = s.Threads[s.Count - 1].ToString("N0"), Watched = watched, Score = last - first });
        }
        return rows.OrderByDescending(r => r.Score).ToList();
    }
}

class PoolTag
{
    public string Name = "";
    public long PagedBytes, NonpagedBytes, Live;
    public long Bytes { get { return PagedBytes + NonpagedBytes; } }
}

class TagRow
{
    public string Tag = "", Owner = "";
    public long Bytes, SinceStart, LastHour, Live;
}

/// <summary>
/// Kernel memory by owner. Drivers mark the kernel memory they take with a four-letter tag; Windows keeps the totals.
/// The tags that grow are where a kernel leak is, and the driver file that carries the tag is its owner.
/// </summary>
class KernelTags
{
    const double Mb = 1048576.0;
    Dictionary<string, long> first;
    readonly List<KeyValuePair<DateTime, Dictionary<string, long>>> recent = new List<KeyValuePair<DateTime, Dictionary<string, long>>>(); // one every 5 minutes, an hour kept
    readonly Dictionary<string, string> owners = new Dictionary<string, string>();
    bool searching;

    public List<PoolTag> Now = new List<PoolTag>();
    public DateTime FirstAt = DateTime.MinValue;

    /// <summary>Every pool tag with its bytes and live allocations (NtQuerySystemInformation, SystemPoolTagInformation).</summary>
    public static List<PoolTag> Read()
    {
        var list = new List<PoolTag>();
        if (IntPtr.Size != 8) return list;
        int length = 1 << 20;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                int returned;
                int status = Native.NtQuerySystemInformation(22, buffer, length, out returned);
                if (status == unchecked((int)0xC0000004) && length < (1 << 26)) { length *= 2; continue; }
                if (status != 0) return list;
                int count = Marshal.ReadInt32(buffer);
                var chars = new byte[4];
                for (int i = 0; i < count; i++)
                {
                    IntPtr e = buffer + 8 + i * 40;
                    Marshal.Copy(e, chars, 0, 4);
                    long paged = (uint)(Marshal.ReadInt32(e, 4) - Marshal.ReadInt32(e, 8)), nonpaged = (uint)(Marshal.ReadInt32(e, 24) - Marshal.ReadInt32(e, 28));
                    list.Add(new PoolTag { Name = Encoding.ASCII.GetString(chars), Live = paged + nonpaged, PagedBytes = Marshal.ReadInt64(e, 16), NonpagedBytes = Marshal.ReadInt64(e, 32) });
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public void Sample(DateTime now)
    {
        var tags = Read();
        if (tags.Count == 0) return;
        Now = tags;
        var bytes = new Dictionary<string, long>();
        foreach (var t in tags) bytes[t.Name] = t.Bytes;
        if (first == null) { first = bytes; FirstAt = now; }
        if (recent.Count == 0 || now - recent[recent.Count - 1].Key >= TimeSpan.FromMinutes(5))
        {
            recent.Add(new KeyValuePair<DateTime, Dictionary<string, long>>(now, bytes));
            while (recent.Count > 13) recent.RemoveAt(0);
        }
    }

    public long Live(string tag)
    {
        var t = Now.FirstOrDefault(x => x.Name == tag);
        return t == null ? -1 : t.Live;
    }

    /// <summary>The largest tags, plus any other that has grown by 20 MB or more since MemControl started.</summary>
    public List<TagRow> Rows(int count)
    {
        var hourAgo = recent.Count > 1 ? recent[0].Value : null;
        Func<Dictionary<string, long>, string, long, long> grown = (then, tag, bytes) =>
        {
            if (then == null) return 0;
            long before;
            return bytes - (then.TryGetValue(tag, out before) ? before : 0);
        };
        var all = Now.Select(t => new TagRow { Tag = t.Name, Bytes = t.Bytes, Live = t.Live, SinceStart = grown(first, t.Name, t.Bytes), LastHour = grown(hourAgo, t.Name, t.Bytes) }).ToList();
        var rows = all.OrderByDescending(r => r.Bytes).Take(count).ToList();
        foreach (var r in all.Where(r => r.SinceStart >= 20 * Mb).OrderByDescending(r => r.SinceStart).Take(8)) if (!rows.Contains(r)) rows.Add(r);
        lock (owners)
            foreach (var r in rows)
            {
                string found;
                r.Owner = Known(r.Tag) ?? (owners.TryGetValue(r.Tag, out found) ? found : "looking…");
            }
        return rows;
    }

    /// <summary>What the common tags of Windows itself stand for; null for a tag that has to be looked up in the driver files.</summary>
    public static string Known(string tag)
    {
        switch (tag)
        {
            case "File": return "Windows: open files (file objects)";
            case "Toke": return "Windows: security tokens";
            case "Proc": return "Windows: process objects";
            case "Thre": return "Windows: thread objects";
            case "Even": case "Sema": case "Muta": return "Windows: events, semaphores and mutexes";
            case "Sect": return "Windows: shared memory sections";
            case "Key ": return "Windows: open registry keys";
            case "Pool": return "Windows: pool bookkeeping";
        }
        if (tag.StartsWith("Ntf", StringComparison.Ordinal) || tag.StartsWith("NtF", StringComparison.Ordinal)) return "NTFS file system (Ntfs.sys)";
        if (tag.StartsWith("FM", StringComparison.Ordinal)) return "Filter Manager: antivirus and sync drivers hook in here (FLTMGR.SYS)";
        if (tag.StartsWith("Mm", StringComparison.Ordinal)) return "Windows memory manager (file cache and program memory maps)";
        if (tag.StartsWith("sm", StringComparison.Ordinal)) return "Windows: memory compression store";
        if (tag.StartsWith("Etw", StringComparison.Ordinal)) return "Windows: event tracing buffers";
        if (tag.StartsWith("CM", StringComparison.Ordinal)) return "Windows registry";
        if (tag.StartsWith("Vad", StringComparison.Ordinal)) return "Windows: memory maps of programs";
        return null;
    }

    static string Company(string file)
    {
        try
        {
            string company = FileVersionInfo.GetVersionInfo(file).CompanyName;
            return string.IsNullOrEmpty(company) ? "" : " (" + company.Trim() + ")";
        }
        catch { return ""; }
    }

    static string Describe(List<string> files)
    {
        if (files.Count == 0) return "not found in any loaded driver";
        var windows = new[] { "ntoskrnl.exe", "hal.dll", "pshed.dll", "win32k.sys", "win32kbase.sys", "win32kfull.sys", "ci.dll", "clfs.sys" };
        var others = files.Where(f => !windows.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList();
        if (others.Count == 0) return "Windows kernel (" + Path.GetFileName(files[0]) + ")";
        if (others.Count > 4) return "a name shared by " + files.Count + " drivers";
        return string.Join(", ", others.Take(3).Select(f => Path.GetFileName(f) + Company(f)));
    }

    /// <summary>
    /// Finds on a background thread which loaded kernel modules carry the given tags: a driver names its memory with
    /// a tag that stands in its file. The files are read once; done is called when the answers are in.
    /// </summary>
    public void FindOwners(IEnumerable<string> tags, Action done)
    {
        List<string> wanted;
        lock (owners)
        {
            if (searching) return;
            wanted = tags.Where(t => t.Length == 4 && Known(t) == null && !owners.ContainsKey(t)).Distinct().ToList();
            if (wanted.Count == 0) return;
            searching = true;
        }
        var thread = new Thread(() =>
        {
            var found = wanted.ToDictionary(t => t, t => new List<string>());
            try
            {
                var patterns = wanted.Select(t => Encoding.ASCII.GetBytes(t)).ToList();
                var starts = new bool[256];
                foreach (var p in patterns) starts[p[0]] = true;
                foreach (var file in Sys.KernelModules())
                {
                    byte[] data;
                    try
                    {
                        if (new FileInfo(file).Length > 400 * 1048576L) continue;
                        data = File.ReadAllBytes(file);
                    }
                    catch { continue; }
                    var hit = new bool[patterns.Count];
                    int left = patterns.Count;
                    for (int i = 0, end = data.Length - 3; i < end && left > 0; i++)
                    {
                        if (!starts[data[i]]) continue;
                        for (int k = 0; k < patterns.Count; k++)
                        {
                            var p = patterns[k];
                            if (hit[k] || data[i] != p[0] || data[i + 1] != p[1] || data[i + 2] != p[2] || data[i + 3] != p[3]) continue;
                            hit[k] = true;
                            left--;
                            found[wanted[k]].Add(file);
                        }
                    }
                }
            }
            catch { }
            lock (owners)
            {
                foreach (var kv in found) owners[kv.Key] = Describe(kv.Value);
                searching = false;
            }
            if (done != null) done();
        });
        thread.IsBackground = true;
        thread.Priority = ThreadPriority.BelowNormal;
        thread.Start();
    }
}

class LeakWindow : Form
{
    readonly Tray tray;
    readonly ListView programs = new ListView(), kernel = new ListView();
    readonly Label summary = new Label(), empty = new Label { AutoSize = true, ForeColor = Color.Gray, BackColor = Color.White, Location = new Point(8, 30) };
    readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();

    public LeakWindow(Tray tray)
    {
        this.tray = tray;
        Text = "MemControl — leak watch";
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(880, Math.Min(680, Screen.PrimaryScreen.WorkingArea.Height - 60));
        MinimumSize = new Size(700, 480);
        BackColor = Color.FromArgb(248, 249, 251);
        const AnchorStyles sides = AnchorStyles.Left | AnchorStyles.Right;
        var bold = new Font("Segoe UI", 9.5f, FontStyle.Bold);

        var head1 = new Label { Text = "Programs that keep growing", Font = bold, AutoSize = true, Left = 16, Top = 12 };
        var note1 = new Label { Text = "Steady growth for hours often means a leak, though a program that is loading or working on something large grows too. Each program is watched for at least 30 minutes before it can appear here.",
            Left = 16, Top = 32, Width = 848, Height = 34, ForeColor = Color.DimGray, Anchor = AnchorStyles.Top | sides };
        programs.SetBounds(16, 70, 848, 170);
        programs.Anchor = AnchorStyles.Top | sides;
        var head2 = new Label { Text = "Windows kernel memory by owner", Font = bold, AutoSize = true, Left = 16, Top = 252 };
        var note2 = new Label { Text = "No program owns this memory, so no cleaner can free it. Drivers mark what they take with a four-letter tag; a tag that keeps growing is a leak, and its owner is the driver to update.",
            Left = 16, Top = 272, Width = 848, Height = 34, ForeColor = Color.DimGray, Anchor = AnchorStyles.Top | sides };
        kernel.SetBounds(16, 310, 848, ClientSize.Height - 310 - 76);
        kernel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | sides;
        summary.SetBounds(16, ClientSize.Height - 68, 848, 56);
        summary.Anchor = AnchorStyles.Bottom | sides;

        foreach (var view in new[] { programs, kernel }) { view.View = View.Details; view.FullRowSelect = true; view.HeaderStyle = ColumnHeaderStyle.Nonclickable; view.ShowItemToolTips = true; }
        programs.Columns.Add("Program", 210); programs.Columns.Add("PID", 70, HorizontalAlignment.Right); programs.Columns.Add("Grows", 90);
        programs.Columns.Add("Growth", 130, HorizontalAlignment.Right); programs.Columns.Add("Speed", 120, HorizontalAlignment.Right);
        programs.Columns.Add("Now", 110, HorizontalAlignment.Right); programs.Columns.Add("Watched", 90, HorizontalAlignment.Right);
        kernel.Columns.Add("Tag", 60); kernel.Columns.Add("Owner", 380); kernel.Columns.Add("MB", 80, HorizontalAlignment.Right);
        kernel.Columns.Add("Since start", 100, HorizontalAlignment.Right); kernel.Columns.Add("Last hour", 90, HorizontalAlignment.Right); kernel.Columns.Add("Objects", 110, HorizontalAlignment.Right);
        // Double-click a program: the process manager, filtered to it.
        programs.DoubleClick += (o, e) => { if (programs.SelectedItems.Count > 0 && programs.SelectedItems[0].SubItems.Count > 1) tray.ShowProcesses(programs.SelectedItems[0].SubItems[1].Text); };

        programs.Controls.Add(empty); // the note shown while the list is empty
        Controls.AddRange(new Control[] { head1, note1, programs, head2, note2, kernel, summary });
        refresh.Interval = 5000;
        refresh.Tick += (o, e) => { if (Visible) Fill(); };
        FormClosing += (o, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); refresh.Stop(); } };
    }

    public void Open()
    {
        Fill();
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        refresh.Start();
    }

    public void Fill()
    {
        var tags = tray.Tags.Rows(14);
        tray.Tags.FindOwners(tags.Select(t => t.Tag), () => { try { if (IsHandleCreated) BeginInvoke((Action)(() => { if (Visible) Fill(); })); } catch { } });
        long zombieProcesses = Math.Max(0, tray.Tags.Live("Proc") - tray.Processes.Count), zombieThreads = Math.Max(0, tray.Tags.Live("Thre") - tray.Processes.Sum(p => (long)p.Threads));
        var s = tray.Last;
        string text = string.Format("Kernel memory: {0:0.0} GB (paged {1:0.0}, nonpaged {2:0.0}). ", s.KernelGb, s.PagedGb, s.NonpagedGb) + tray.KernelForecast
            + (tray.Tags.Live("Proc") >= 0 ? string.Format("\nEnded programs that something still holds on to: {0:N0} processes and {1:N0} threads. A few hundred is normal; many thousands are a leak in the program that holds them.", zombieProcesses, zombieThreads) : "");
        SetRows(tray.Growth.Growing(1), tags, text, tray.Growth.Longest);
    }

    static string Signed(long bytes) { double mb = bytes / 1048576.0; return Math.Abs(mb) < 0.5 ? "0" : (mb > 0 ? "+" : "") + mb.ToString("N0"); }

    /// <summary>Shows the given rows (also used to draw the window with example figures).</summary>
    public void SetRows(List<GrowthRow> growing, List<TagRow> tags, string text, int minutesWatched)
    {
        programs.BeginUpdate();
        programs.Items.Clear();
        foreach (var r in growing) programs.Items.Add(new ListViewItem(new[] { r.Name, r.Pid.ToString(), r.What, r.Growth, r.PerHour, r.Now, r.Watched }));
        programs.EndUpdate();
        empty.Text = minutesWatched < 30 ? "Still watching: " + minutesWatched + " of the first 30 minutes." : "Nothing has been growing steadily.";
        empty.Visible = growing.Count == 0;
        kernel.BeginUpdate();
        kernel.Items.Clear();
        foreach (var t in tags)
        {
            var item = new ListViewItem(new[] { t.Tag, t.Owner, (t.Bytes / 1048576.0).ToString("N0"), Signed(t.SinceStart), Signed(t.LastHour), t.Live.ToString("N0") });
            if (t.SinceStart >= 100 * 1048576L) item.ForeColor = Color.FromArgb(200, 40, 50);
            else if (t.SinceStart >= 20 * 1048576L) item.ForeColor = Color.FromArgb(190, 120, 0);
            kernel.Items.Add(item);
        }
        kernel.EndUpdate();
        summary.Text = text;
    }
}
