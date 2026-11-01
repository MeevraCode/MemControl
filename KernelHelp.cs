// "What is this? Why does it grow?": the kernel memory number explained in plain words, with this PC's own numbers.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

static class KernelHelp
{
    public static void Show(IWin32Window owner, Snapshot s)
    {
        string text =
"WHAT THIS NUMBER IS\r\n" +
"Windows itself and its drivers (graphics, disk, network, antivirus, cloud sync) keep their own memory, called the kernel pools. " +
"Task Manager counts it as used RAM, but no program owns it, so no program in Task Manager shows it.\r\n" +
string.Format("  • Paged pool: {0:0.0} GB. It can be moved to the page file.\r\n", s.PagedGb) +
string.Format("  • Nonpaged pool: {0:0.0} GB. It must stay in RAM.\r\n", s.NonpagedGb) +
string.Format("  • Total: {0:0.0} GB. Right after a restart it is normally 1–2 GB.\r\n\r\n", s.KernelGb) +
"WHY IT GROWS\r\n" +
"1. Normal work (it comes back by itself). While you build code, run tests, copy many files or play games, Windows keeps " +
"more file and cache records. This part shrinks again when the work stops.\r\n" +
"2. A driver leak (it never comes back). Some drivers take a piece of kernel memory every time a program starts, and " +
"never give it back, even after the program has closed. This grows a little with every program start and adds up " +
"over days. Busy PCs (developer tools, test runs, browsers with many tabs) start thousands of processes a day.\r\n\r\n" +
"HOW MEMCONTROL TELLS THEM APART\r\n" +
"A leaked kernel object that nobody gives back stays counted as a live file object. MemControl reads that count " +
"from Windows (the \"File\" pool tag) and estimates the memory it pins (about 1.2 KB each).\r\n" +
(s.StuckFiles >= 0
    ? string.Format("  • Now: {0:0.00} million live file objects, a likely leak of about {1:0.0} GB.\r\n", s.StuckFiles / 1e6, s.LeakGb)
    : "  • This Windows version did not report the count.\r\n") +
"  • A few hundred thousand file objects is normal. Millions that keep climbing day after day are a leak.\r\n\r\n" +
"A KNOWN CAUSE\r\n" +
"Some AMD Radeon graphics drivers (amdkmdag.sys, also on laptops with Radeon integrated graphics such as the 780M) leak " +
"2 file objects every time a process starts and ends. It was traced with Windows Performance Recorder and reported to AMD. " +
"Other drivers can leak in the same way. Updating the graphics driver can help once a fixed version is out.\r\n\r\n" +
"WHAT FREES IT\r\n" +
"  • Restart frees all of it.\r\n" +
"  • Shut down does not free it while Fast Startup is on, because Windows saves the kernel and restores it at power-on.\r\n" +
"  • Cleaning (Clean now) cannot free leaked kernel memory. It frees programs' memory and caches, which is a different kind of memory.\r\n" +
"  • Ending programs does not free it either, because the leak stays behind after the program has closed.\r\n\r\n" +
"WHAT THE COLORS MEAN\r\n" +
"  • Green: under 4 GB. Nothing to do.\r\n" +
"  • Yellow: 4–8 GB. Restart when convenient.\r\n" +
"  • Red: over 8 GB. Save your work and Restart; the PC may slow down or run out of memory.";

        using (var form = new Form())
        {
            form.Text = "MemControl — kernel memory explained";
            form.Font = new Font("Segoe UI", 10f);
            form.StartPosition = FormStartPosition.CenterParent;
            form.ClientSize = new Size(640, 560);
            form.MinimizeBox = false;
            var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = text, BackColor = Color.White,
                Left = 12, Top = 12, Width = 616, Height = 490, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            var link = new LinkLabel { Text = "The driver leak report (GitHub)", AutoSize = true, Left = 12, Top = 520, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            link.LinkClicked += (o, e) => Process.Start("https://github.com/anthropics/claude-code/issues/96722");
            var close = new Button { Text = "Close", Width = 100, Height = 30, Left = 528, Top = 516, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.OK };
            form.Controls.AddRange(new Control[] { box, link, close });
            form.AcceptButton = close;
            form.Shown += (o, e) => box.Select(0, 0);
            form.ShowDialog(owner);
        }
    }
}
