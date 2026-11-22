// "Always run as administrator" without a prompt at every start.
// MemControl copies itself into Program Files, where only administrators can change files, and creates a scheduled
// task named "MemControl" (no trigger, run on demand, highest privileges) that starts that copy. Both need
// administrator rights once (one UAC prompt). After that a copy started without the rights runs the task and steps
// aside (Program.Main). The task starts a file that programs without the rights cannot replace, so they cannot use it
// to get the rights. After MemControl.exe is updated the copy is older, and renewing it takes one more prompt.
// The administrator copy keeps the settings and the log where they were ("/home") and reads and writes them as the
// plain user (AsUser): otherwise a program without the rights could point those file names at files of its choice.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;

static class AdminTask
{
    public enum Status { Missing, Ready, Outdated, OtherCopy, Unknown }

    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool MoveFileEx(string existing, string replacement, int flags);

    public static string Name { get { return "MemControl" + Program.Instance; } }

    /// <summary>Where the copy that runs as administrator lives: a folder only administrators can change.</summary>
    public static string ProtectedExe
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MemControl" + Program.Instance, "MemControl.exe"); }
    }

    public static string Arguments(string home, string instanceArgs) { return "/takeover /home \"" + home.TrimEnd('\\') + "\"" + instanceArgs; }

    public static string UserSid()
    {
        using (var id = WindowsIdentity.GetCurrent()) return id.User.Value;
    }

    /// <summary>False for a standard account: there the task could not give administrator rights.</summary>
    public static bool Possible()
    {
        if (Native.IsAdmin()) return true;
        IntPtr token;
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x8, out token)) return false; // TOKEN_QUERY
        try
        {
            int type, returned;
            return GetTokenInformation(token, 18, out type, 4, out returned) && type == 3; // TokenElevationType: 3 = an administrator's limited token
        }
        finally { Native.CloseHandle(token); }
    }

    // The root folder of the Task Scheduler (its scripting interface, so no extra library is needed).
    static dynamic Folder()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        service.Connect();
        return service.GetFolder("\\");
    }

    static bool Same(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>True for "Program Files\MemControl…\file": a folder whose name starts with MemControl, directly under Program Files.</summary>
    public static bool UnderProgramFiles(string exe)
    {
        try
        {
            string root = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).TrimEnd('\\');
            string folder = Path.GetDirectoryName(Path.GetFullPath(exe));
            return root != "" && string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(folder).StartsWith("MemControl", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool SameContent(string a, string b)
    {
        try
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(a))) == Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(b)));
        }
        catch { return false; }
    }

    public static Status State() { return State(Name, ProtectedExe, Program.Exe, Program.Folder); }

    /// <summary>
    /// Ready: the task is enabled, starts the protected copy, and that copy is this very exe: either the running file
    /// itself, or a file with the same content that was set up from this folder. Outdated: the copy is older or gone.
    /// OtherCopy: the task starts something else (an earlier version pointed it at the exe in your own folder).
    /// </summary>
    public static Status State(string name, string protectedExe, string exe, string home)
    {
        try
        {
            dynamic task = Folder().GetTask(name);
            if (!(bool)task.Enabled) return Status.OtherCopy;
            foreach (dynamic action in task.Definition.Actions)
            {
                if ((int)action.Type != 0) continue;
                if (!Same((string)action.Path, protectedExe)) return Status.OtherCopy;
                if (!File.Exists(protectedExe)) return Status.Outdated;
                if (Same(exe, protectedExe)) return Status.Ready;
                string arguments = (string)action.Arguments ?? "";
                bool fromHere = arguments.IndexOf("/home \"" + home.TrimEnd('\\') + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
                return fromHere && SameContent(exe, protectedExe) ? Status.Ready : Status.Outdated;
            }
            return Status.OtherCopy;
        }
        catch (FileNotFoundException) { return Status.Missing; }
        catch (COMException e) { return e.ErrorCode == unchecked((int)0x80070002) ? Status.Missing : Status.Unknown; }
        catch (Exception) { return Status.Unknown; }
    }

    public static bool Run() { return Run(Name); }

    /// <summary>Starts the task now. No prompt appears: Windows already holds the consent given when the task was created.</summary>
    public static bool Run(string name)
    {
        try
        {
            dynamic task = Folder().GetTask(name);
            try { task.RunEx(null, 2, 0, null); } // 2: ignore conditions such as "only on mains power"
            catch (Exception) { task.Run(null); }
            return true;
        }
        catch (Exception e) { Log.Write("the task \"" + name + "\" could not be started: " + e.Message); return false; }
    }

    /// <summary>Creates or replaces the task; null on success, else the reason. With highest = true it needs administrator rights.</summary>
    public static string Create(string name, string exe, string arguments, string sid, bool highest)
    {
        try { Folder().RegisterTask(name, Xml(exe, arguments, sid, highest), 6, null, null, 3, null); return null; } // 6: create or update, 3: the logged-on user's token
        catch (Exception e) { return e.Message; }
    }

    public static string Delete(string name)
    {
        try { Folder().DeleteTask(name, 0); return null; }
        catch (FileNotFoundException) { return null; }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>
    /// Copies the exe into its protected place and creates the task that starts the copy. Needs administrator rights.
    /// Null on success, else the reason.
    /// </summary>
    public static string Install(string name, string sourceExe, string protectedExe, string home, string sid, string instanceArgs, bool highest = true)
    {
        // With highest privileges the copy goes only into its own folder directly under Program Files, whatever was asked.
        if (highest && !UnderProgramFiles(protectedExe)) return "the place for the copy is not a MemControl folder under Program Files";
        try
        {
            string folder = Path.GetDirectoryName(protectedExe);
            Directory.CreateDirectory(folder);
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return "the folder " + folder + " is a link, not a folder";
            if (!Same(sourceExe, protectedExe)) File.Copy(sourceExe, protectedExe, true);
        }
        catch (Exception e) { return "copying MemControl into Program Files failed: " + e.Message; }
        return Create(name, protectedExe, Arguments(home, instanceArgs), sid, highest);
    }

    /// <summary>Removes the task and the protected copy. A copy that is running is removed by Windows at the next restart.</summary>
    public static string Uninstall(string name, string protectedExe)
    {
        string why = Delete(name);
        string folder = Path.GetDirectoryName(protectedExe);
        try
        {
            if (File.Exists(protectedExe)) File.Delete(protectedExe);
            if (Directory.Exists(folder)) Directory.Delete(folder, false);
        }
        catch
        {
            MoveFileEx(protectedExe, null, 4); // MOVEFILE_DELAY_UNTIL_REBOOT
            MoveFileEx(folder, null, 4);
        }
        return why;
    }

    /// <summary>
    /// The task definition. Windows' defaults would stop the task after 72 hours, refuse to start it on battery and run it
    /// at low priority, so each of these is set here.
    /// </summary>
    public static string Xml(string exe, string arguments, string sid, bool highest)
    {
        Func<string, string> x = SecurityElement.Escape;
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
            + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
            + "  <RegistrationInfo><Description>" + x("Starts MemControl with administrator rights without a prompt (its option \"Always run as administrator\").") + "</Description></RegistrationInfo>\r\n"
            + "  <Principals><Principal id=\"Author\"><UserId>" + x(sid) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>" + (highest ? "HighestAvailable" : "LeastPrivilege") + "</RunLevel></Principal></Principals>\r\n"
            + "  <Settings>"
            + "<MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>"
            + "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
            + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"
            + "<AllowHardTerminate>true</AllowHardTerminate>"
            + "<StartWhenAvailable>false</StartWhenAvailable>"
            + "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>"
            + "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>"
            + "<AllowStartOnDemand>true</AllowStartOnDemand>"
            + "<Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>"
            + "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority>"
            + "</Settings>\r\n"
            + "  <Actions Context=\"Author\"><Exec><Command>" + x(exe) + "</Command><Arguments>" + x(arguments) + "</Arguments><WorkingDirectory>" + x(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\r\n"
            + "</Task>";
    }

    /// <summary>
    /// "MemControl.exe /admintask create SID HOME" or "/admintask remove": MemControl runs itself this way with
    /// administrator rights. HOME is the folder with the settings and the log.
    /// </summary>
    public static int Command(string[] a)
    {
        string what = a.Length > 0 ? a[0].ToLowerInvariant() : "";
        string why = !Native.IsAdmin() ? "administrator rights were not given"
            : what == "create" && a.Length > 2 && Path.IsPathRooted(a[2]) ? Install(Name, Program.Exe, ProtectedExe, a[2], a[1], Program.InstanceArgs)
            : what == "remove" ? Uninstall(Name, ProtectedExe)
            : "unknown command";
        Log.Write("always run as administrator, " + (what == "" ? "setup" : what) + ": " + (why ?? "done"));
        return why == null ? 0 : 1;
    }

    /// <summary>Runs "MemControl.exe /admintask ..." with administrator rights (one UAC prompt) and waits for it; null when it ran, else the reason.</summary>
    public static string Elevated(string arguments)
    {
        try
        {
            using (var p = Process.Start(new ProcessStartInfo(Program.Exe, "/admintask " + arguments + Program.InstanceArgs) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(Program.Exe) }))
            {
                if (p == null) return "the setup did not start";
                return p.WaitForExit(60000) ? null : "the setup did not finish within a minute";
            }
        }
        catch (Win32Exception) { return "the administrator prompt was declined"; }
    }
}

/// <summary>
/// A copy with administrator rights reads and writes its settings, log and history as the plain user. Those files lie in
/// a folder that programs without the rights can change; they could point the file names at other files and have them
/// written with administrator rights.
/// </summary>
static class AsUser
{
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool CreateRestrictedToken(IntPtr token, int flags, int disableCount, IntPtr disable, int deleteCount, IntPtr delete, int restrictCount, IntPtr restrict, out IntPtr restricted);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr token, int access, IntPtr attributes, int level, int type, out IntPtr duplicate);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool ConvertStringSidToSid(string text, out IntPtr sid);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

    static readonly bool elevated = Native.IsAdmin();
    static readonly object gate = new object();
    static IntPtr plain = IntPtr.Zero;
    static bool tried;

    /// <summary>A token of this user without administrator rights and at medium integrity, for impersonation; zero when it cannot be made.</summary>
    public static IntPtr PlainToken()
    {
        lock (gate)
        {
            if (tried) return plain;
            tried = true;
            IntPtr own = IntPtr.Zero, restricted = IntPtr.Zero, medium = IntPtr.Zero, label = IntPtr.Zero;
            try
            {
                if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0xF01FF, out own)) return IntPtr.Zero; // TOKEN_ALL_ACCESS
                if (!CreateRestrictedToken(own, 0x1 | 0x4, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted)) return IntPtr.Zero; // DISABLE_MAX_PRIVILEGE | LUA_TOKEN
                if (!ConvertStringSidToSid("S-1-16-8192", out medium)) return IntPtr.Zero; // medium integrity
                label = Marshal.AllocHGlobal(32); // TOKEN_MANDATORY_LABEL { SID*, attributes }
                Marshal.WriteIntPtr(label, medium);
                Marshal.WriteInt64(label, IntPtr.Size, 0x20); // SE_GROUP_INTEGRITY
                if (!SetTokenInformation(restricted, 25, label, IntPtr.Size + 8 + 12)) return IntPtr.Zero; // TokenIntegrityLevel
                IntPtr copy;
                if (!DuplicateTokenEx(restricted, 0x02000000, IntPtr.Zero, 2, 2, out copy)) return IntPtr.Zero; // MAXIMUM_ALLOWED, SecurityImpersonation, TokenImpersonation
                plain = copy;
                return plain;
            }
            catch { return IntPtr.Zero; }
            finally
            {
                if (label != IntPtr.Zero) Marshal.FreeHGlobal(label);
                if (medium != IntPtr.Zero) LocalFree(medium);
                if (restricted != IntPtr.Zero) Native.CloseHandle(restricted);
                if (own != IntPtr.Zero) Native.CloseHandle(own);
            }
        }
    }

    /// <summary>
    /// Runs a file operation as the plain user when this copy has administrator rights, and directly otherwise. If the
    /// plain token cannot be made, a read still runs and a write is skipped.
    /// </summary>
    public static void Run(Action action, bool writes)
    {
        if (!elevated) { action(); return; }
        IntPtr token = PlainToken();
        if (token == IntPtr.Zero) { if (!writes) action(); return; }
        using (WindowsIdentity.Impersonate(token)) action();
    }
}

/// <summary>
/// The named objects that tell copies of MemControl about each other. Each carries a rule that lets this user's programs
/// signal it; without the rule a copy without administrator rights could not reach the elevated one.
/// </summary>
static class Shared
{
    static SecurityIdentifier User()
    {
        using (var id = WindowsIdentity.GetCurrent()) return id.User;
    }

    public static Mutex OpenMutex(string name, out bool created)
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(User(), MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
        return new Mutex(true, name, out created, security);
    }

    public static EventWaitHandle OpenEvent(string name)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(User(), EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
        bool created;
        try { return new EventWaitHandle(false, EventResetMode.AutoReset, name, out created, security); }
        catch (UnauthorizedAccessException) { return EventWaitHandle.OpenExisting(name, EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify); } // it exists and grants only these two rights
    }
}
