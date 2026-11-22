# MemControl

A small Windows tray app that frees RAM, in the spirit of [Mem Reduct](https://github.com/henrypp/memreduct). It also
watches for something no RAM cleaner can fix: a **kernel memory leak** caused by a driver.

## What it does

- **Tray icon:** shows the RAM use in %. It is green below 75%, yellow from 75% and red from 90% or when a kernel leak is high.
- **Clean now:** from the window, the tray menu, a double-click on the icon, or **Ctrl+Shift+F1**. It cleans the areas you pick:
  - working sets of programs;
  - the system file cache;
  - the modified page list;
  - the standby list (low priority only, or all of it);
  - page combining;
  - the registry cache.
- **Automatic cleaning:** when RAM use reaches a set percentage, every N minutes, or both. It cleans at most once every 2 minutes.
  - **It backs off.** Programs take their memory straight back, so cleaning in a loop only slows the PC. After each
    automatic cleaning the next one waits longer: 5, 15, then 30 minutes. Ten calm minutes under the limit start it
    over, and a jump of 10 points in RAM use cuts the wait. The window shows when the next one may run.
  - **Gentle** (on by default): automatic cleaning empties only programs that have done nothing for 3 minutes, and
    never the one in front.
  - **Pause during games** (on by default): no automatic cleaning while a full-screen program is in front. Cleaning by
    hand still works then and leaves that program alone.
  - **Never clean:** a list of program names that no cleaning touches. Type them in the window, or right-click a
    program in the process manager.
- **Notifications:** says how much was freed.
- **Kernel leak watch:** shows how much memory the Windows kernel holds (paged and nonpaged pool) and how many file objects are alive. It warns at 4 GB and 8 GB.
  - Cleaning cannot free this memory. When a driver leaks pool on every process start, only a **Restart** frees it.
  - The window has a Restart button that always asks first.
  - The window splits the number into paged and nonpaged pool, plus a **likely leak** estimate (live file objects at
    about 1.2 KB each), so you can tell normal work, which shrinks again, from a leak, which never does.
  - "What is this? Why does it grow?" explains it all in plain words using your PC's own numbers.
- **Commit warning:** shows how much memory programs have reserved (the commit charge) against the largest limit
  Windows can reach by enlarging the page file. It warns at 85% and 93% and names the largest programs.
  - When commit reaches a limit that cannot rise, programs fail with out-of-memory errors, even while RAM use looks moderate.
  - Cleaning cannot lower commit. Only closing programs can.
  - "Explain" in the window tells it in plain words using your PC's own numbers.
- **Process manager** (tray menu → Processes…, the window's "Manage processes…", or `MemControl.exe /processes`):
  - every process with its CPU %, memory, working set and path;
  - a filter by name, path or PID, sorting by any column, and a "Group by program" view;
  - **End process**, **End process tree** (the process and everything it started), **End all with this name**,
    **Trim memory** (moves a process's memory out of RAM) and **Open file location**, from the buttons, the right-click
    menu or the Delete key.
  - It always shows what will be closed and asks first.
  - "Never clean this program" in the right-click menu puts the program on the never-clean list or takes it off.
  - Windows' critical processes (System, csrss, winlogon, lsass, services, svchost, dwm…) and MemControl itself are
    protected and shown in gray. Ending them would crash Windows or log you off.
- **Window:**
  - RAM, cache and commit;
  - the kernel pool;
  - a 60-minute graph of RAM % and kernel GB;
  - the top memory users, grouped by program;
  - the last 20 cleanings.

## Administrator rights

Without administrator rights, MemControl trims only the working sets of your own programs. Every other area needs
administrator rights; this works the same way in Mem Reduct.

The window's **Run as administrator** button restarts MemControl elevated, after a UAC prompt.

**Always (no prompt)** beside it makes that permanent. Windows asks once, and MemControl creates a scheduled task named
`MemControl` (no trigger, highest privileges). From then on a copy started without the rights, by the Startup shortcut
or by you, runs that task and steps aside for the elevated copy. Untick the box while running as administrator to
remove the task.

The cost: the task starts `MemControl.exe` from its folder with administrator rights and no prompt. Any program running
under your account that can replace that file gets the same rights. Keep MemControl in a folder only you can write to,
or leave the box unticked.

## Why the kernel watch

On some PCs a display driver keeps a couple of kernel file objects for every process that starts and never releases
them. After a week of heavy use (builds, test runs) this can reach 15–20 GB.

- **Task Manager** shows the RAM as used, but no program owns it.
- **RAM cleaners** cannot touch it.
- **Shut down** does not free it either while Fast Startup is on, because Windows restores the kernel session.
- **Restart** frees it.

MemControl shows this and tells you when to restart.

## Build and install

You need no SDK. Windows already has the C# compiler for .NET Framework 4.x.

```
build.cmd
powershell -ExecutionPolicy Bypass -File install.ps1
```

`install.ps1` creates a Desktop shortcut that opens the window (`/show`) and a Startup shortcut that starts MemControl in
the tray, then starts it. Settings are saved in `memcontrol.ini` beside the exe, and cleanings are logged to `memcontrol.log`.
Four warning levels have no box in the window and are set in `memcontrol.ini`: `kernelYellowGb`, `kernelRedGb`,
`commitYellow` and `commitRed` (percent).

## Tests

`tests\run-tests.cmd` builds and runs two test programs. Exit code 0 means everything passed.

- `ProcessToolsTest` checks the protection list, the process-tree discovery, Trim, and ending a throwaway process tree.
- `SmartTest` checks the back-off policy, the never-clean list, the process snapshot, activity tracking, leaving
  processes alone while cleaning (on throwaway processes), the commit ceiling, the signals between two copies, and a
  throwaway scheduled task. That task uses least privilege. The real one needs administrator rights, so the tests do not create it.

## Command line

- `MemControl.exe /processes`: opens the process manager.

- `MemControl.exe /show`: opens the window. If MemControl is already running, the running copy opens its window.
- `MemControl.exe /clean`: cleans once with the saved areas, writes the result to `memcontrol.log` and exits.
- `MemControl.exe /takeover`: replaces the running copy. The scheduled task of "Always (no prompt)" starts MemControl this way.

## How it works

MemControl calls the same native Windows functions as other memory cleaners:

- `NtSetSystemInformation` with `SystemMemoryListInformation` (empty working sets, flush the modified list, purge the standby list), `SystemCombinePhysicalMemoryInformation` and `SystemRegistryReconciliationInformation`;
- `SetSystemFileCacheSize` for the system file cache;
- `EmptyWorkingSet` per process.

The kernel numbers come from `GetPerformanceInfo` and `NtQuerySystemInformation(SystemPoolTagInformation)`.

Smart cleaning reads every process from one `NtQuerySystemInformation(SystemProcessInformation)` call, asks
`SHQueryUserNotificationState` whether a full-screen program is in front, and empties working sets one process at a
time when some must be left alone. The commit ceiling comes from the `PagingFiles` registry value and the page-file
rules that Microsoft documents. The scheduled task is made through the Task Scheduler's scripting interface.

## License

MIT. See [LICENSE](LICENSE).
