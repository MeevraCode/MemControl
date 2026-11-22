# CODEMAP — MemControl

Windows tray programa: RAM valymas, kernel nuotėkio stebėjimas, commit įspėjimas, procesų tvarkyklė.
C# 5, WinForms, .NET Framework 4.x, vienas exe, jokių išorinių bibliotekų.

## Failai

| Failas | Paskirtis |
|---|---|
| `MemControl.cs` | Program, Area, Log, Settings, Snapshot, Native, Cleaner, Tray, MainWindow, Bar, Graph |
| `Smart.cs` | ProcInfo, Sys, ProgramList, CleanOptions, Activity, AutoPolicy, CommitState, PageFile |
| `AdminTask.cs` | AdminTask (suplanuota užduotis paleidimui su teisėmis), Shared (vardiniai objektai su prieigos taisykle) |
| `Processes.cs` | ProcessTools (veiksmai su procesais), ProcessWindow (procesų tvarkyklės langas) |
| `KernelHelp.cs` | KernelHelp, TextWindow (bendras paaiškinimo langas), CommitHelp |
| `build.cmd` | Kompiliuoja į `MemControl.exe` šalia šaltinių |
| `install.ps1` | Desktop (`/show`) ir Startup nuorodos, paleidžia programą (be administratoriaus teisių) |
| `tests/ProcessToolsTest.cs`, `tests/SmartTest.cs`, `tests/run-tests.cmd` | Testai, exit 0 = visi praėjo |
| `memcontrol.ini`, `memcontrol.log` | Nustatymai ir žurnalas šalia exe (gitignore) |

## Komandos

- Build: `build.cmd`. Testai: `tests\run-tests.cmd`. Įdiegimas: `powershell -ExecutionPolicy Bypass -File install.ps1`.
- Komandinė eilutė: `/show`, `/processes`, `/clean`, `/takeover`, `/admintask create SID|remove`, `/instance NAME` (tik testams).
- `tests\SmartTest.exe /window FILE.png` nupiešia langą už ekrano; `/auto` paleidžia vieną švelnų automatinį valymą;
  `/task create NAME EXE ARGS` ir `/task delete NAME` sukuria ir pašalina užduotį be aukštesnių teisių.

## Įėjimo taškai ir kopijų bendravimas

- `Program.Main` `MemControl.cs:28`. Vardiniai objektai (`Local\MemControl…` + `Program.Instance`): mutex ir įvykiai
  `Show`, `Processes`, `Quit`; visi kuriami per `Shared.OpenMutex` `AdminTask.cs:164` ir `Shared.OpenEvent` `:171`.
- `/takeover`: nauja kopija nustato `Quit`, laukia mutex iki 15 s, tada `quit.Reset()` (`MemControl.cs:48–55`).
- Perdavimas: kopija be teisių, kai `alwaysAdmin=1` ir užduotis `Ready`, kviečia `HandOver` `MemControl.cs:82`
  (paleidžia užduotį, paleidžia mutex, laukia, kol jį paims kita kopija). Kopija su `/takeover` pati niekada neperduoda.
- `Tray` (ApplicationContext) `MemControl.cs:452`; 2 s laikmatis kviečia `Update` `:513`; 500 ms laikmatis klauso įvykių (`:491` aplink).

## Duomenų srautas

`Tray.Update` `:513` → `Native.Read` `:269` (`GetPerformanceInfo`, `LiveFileObjects` `:291`) → `Snapshot` `:208`
→ `History` (kas 10 s, 360 taškų, tik atmintyje)
→ kas 10 s `Sample` `:541` (`Sys.Processes` `Smart.cs:29` → `Activity.Sample`, `Processes`, `Largest`)
→ kas 30 s `PageFile.Read` `Smart.cs:221` (commit lubos)
→ `SetIcon` `:731`, `AutoClean` `:557`, `KernelWatch` `:619`, `CommitWatch` `:582`, `MainWindow.Refresh` `:929`.

Viskas UI gijoje; valymas per `BackgroundWorker` (`CleanNow` `:635`), kuriam `Options` `:602` UI gijoje paruošia nekintamus sąrašus.

## Pagrindinės vietos, MemControl.cs

- `Settings` `:135` (`Load` `:154`). Nauji raktai: `gentle`, `pauseFullScreen`, `never`, `commitWarn`, `commitYellow`, `commitRed`, `alwaysAdmin`.
- `Native.LiveFileObjects` `:291`: pool žymų lentelė, naudojama tik eilutė „File" (įrašas 40 B, baitai @16 ir @32).
- `Cleaner.Clean(areas, options)` `:346`. Working sets: jei nėra ką palikti ir yra teisės, viena sisteminė komanda; kitaip
  `TrimProcesses(Candidates(), options, out left)` `:436` po vieną procesą (su teisėmis įjungiama SeDebugPrivilege).
- `Tray.AutoClean` `:557`: 2 min grindys, pauzė pilno ekrano metu, `AutoPolicy` sprendžia pagal RAM, laikmatis „kas N min" kaip anksčiau.
- `Tray.CommitWatch` `:582`: lygis kyla iškart, krenta tik 3 punktais žemiau ribos; raudonas kartojamas kas 30 min.
- `Tray.Options` `:602`: visada „Never clean" sąrašas; pilno ekrano metu ir priekinė programa; automatiniame švelniame valyme
  dar ir visi procesai, kurie nebuvo ramūs 3 min.
- `Tray.RestartAsAdmin` `:670` (per užduotį, jei `Ready`; kitaip `ElevateWithPrompt` `:678`), `SetAlwaysAdmin` `:689`.
- `MainWindow` `:780` (760×680), konstruktorius `:798`, `SaveNever` `:922`, `Refresh` `:929`, `FillTop` `:970`.

## Pagrindinės vietos, Smart.cs

- `Sys.Processes` `:29`: `NtQuerySystemInformation(5)`, poslinkiai x64 (Threads@4, PrivateWS@8, Create@32, User@40, Kernel@48,
  NameLen@56, NamePtr@64, Pid@80, Ppid@88, Handles@96, WS@144, PrivateBytes@200). 32 bitų sistemoje grąžina tuščią sąrašą.
- `Sys.FullScreen` `:80` (`SHQueryUserNotificationState` 2, 3 arba 4), `Sys.Largest` `:87`.
- `ProgramList` `:97`, `CleanOptions` `:115`, `Activity` `:129` (užimtas = bent 1 % vieno branduolio nuo praeito mėginio).
- `AutoPolicy` `:166`, `Due` `:182`: laukimai 2, 5, 15, 30 min; 10 ramių min po riba pradeda iš naujo; +10 punktų nuo paskutinio valymo sutrumpina.
- `CommitState` `:210`, `PageFile.Read` `:221`, `PageFile.MaxGb` `:251` (registro `PagingFiles`; sisteminis failas auga iki
  3 × RAM arba 4 GB, ne daugiau kaip 1/8 tomo ir ne daugiau nei laisva vieta minus 1 GB).

## Pagrindinės vietos, AdminTask.cs

- `Possible` `:31` (TokenElevationType 3), `State` `:55`, `Run` `:73` (`RunEx` su vėliava 2), `Create` `:86`, `Xml` `:103`,
  `Command` `:127` (`/admintask`), `Elevated` `:139` (`runas`, vienas UAC langas).
- Užduotis: be trigerio, `HighestAvailable`, `InteractiveToken`, `ExecutionTimeLimit PT0S`, `Priority 4`, baterijos sąlygos išjungtos.
- Task Scheduler pasiekiamas per `dynamic` ir `Schedule.Service`, todėl reikia `/r:Microsoft.CSharp.dll`.

## Pagrindinės vietos, Processes.cs ir KernelHelp.cs

- `ProcessTools` `Processes.cs:15` (`Protected`, `Parents`, `Tree`, `PathOf`, `End`, `Trim`), `ProcessWindow` `:110`;
  kontekstinis meniu „Never clean this program" keičia `Settings.Exclude`.
- `KernelHelp` `KernelHelp.cs:7`, `TextWindow.Fill` `:55`, `CommitHelp.Show` `:75`.

## Taisyklės

- Tik C# 5 sintaksė (Windows įmontuotas `csc.exe`): be `$""`, `?.`, `out var`, `nameof`, `=>` nariuose. `dynamic` leidžiamas.
- Naujas `.cs` failas arba nauja `/r:` turi atsirasti ir `build.cmd`, ir `tests/run-tests.cmd`.
- Programos tekstai ir README anglų kalba.
- Saugomi Windows procesai niekada neužbaigiami. Viskas, kas ką nors uždaro ar perkrauna, pirma klausia.
- Testai negali liesti savininko programų: valymą bandyti tik su savo laikinais procesais arba kai visi kiti palikti ramybėje.
- Exe neturi manifesto, todėl `Environment.OSVersion` rodo 6.2; tikrai versijai reikia `RtlGetVersion`.
