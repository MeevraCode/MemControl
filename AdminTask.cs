// "Always run as administrator" without a prompt at every start. A scheduled task named "MemControl" (no trigger, run
// on demand, highest privileges) starts MemControl with administrator rights. Creating or removing the task needs the
// rights once (one UAC prompt). After that a copy started without them runs the task and steps aside (Program.Main).
// The cost: the task starts MemControl.exe from this folder with administrator rights and no prompt, so any program
// running under your account that can replace that file can get the same rights.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

static class AdminTask
{
    public enum Status { Missing, Ready, OtherCopy, Unknown }

    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);

    public static string Name { get { return "MemControl" + Program.Instance; } }
    public static string Arguments { get { return "/takeover" + Program.InstanceArgs; } }

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

    public static Status State() { return State(Name, Program.Exe); }

    /// <summary>Ready: the task exists, is enabled and starts this very exe.</summary>
    public static Status State(string name, string exe)
    {
        try
        {
            dynamic task = Folder().GetTask(name);
            if (!(bool)task.Enabled) return Status.OtherCopy;
            foreach (dynamic action in task.Definition.Actions)
                if ((int)action.Type == 0 && string.Equals((string)action.Path, exe, StringComparison.OrdinalIgnoreCase)) return Status.Ready;
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

    /// <summary>"MemControl.exe /admintask create SID" or "/admintask remove": MemControl runs itself this way with administrator rights.</summary>
    public static int Command(string[] a)
    {
        string what = a.Length > 0 ? a[0].ToLowerInvariant() : "";
        string why = !Native.IsAdmin() ? "administrator rights were not given"
            : what == "create" && a.Length > 1 ? Create(Name, Program.Exe, Arguments, a[1], true)
            : what == "remove" ? Delete(Name)
            : "unknown command";
        Log.Write("always run as administrator, " + (what == "" ? "setup" : what) + ": " + (why ?? "done"));
        return why == null ? 0 : 1;
    }

    /// <summary>Runs "MemControl.exe /admintask ..." with administrator rights (one UAC prompt) and waits for it; null when it ran, else the reason.</summary>
    public static string Elevated(string arguments)
    {
        try
        {
            using (var p = Process.Start(new ProcessStartInfo(Program.Exe, "/admintask " + arguments + Program.InstanceArgs) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Program.Folder }))
            {
                if (p == null) return "the setup did not start";
                return p.WaitForExit(60000) ? null : "the setup did not finish within a minute";
            }
        }
        catch (Win32Exception) { return "the administrator prompt was declined"; }
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
