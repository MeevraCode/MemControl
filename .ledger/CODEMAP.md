# CODEMAP — MemControl 1.1.0

Windows tray programa: RAM valymas, kernel nuotėkio stebėjimas, „Kodėl lėta?", nuotėkių sekimas, procesų tvarkyklė.
C# 5, WinForms, .NET Framework 4.x, vienas exe, jokių išorinių bibliotekų.

## Failai

| Failas | Paskirtis |
|---|---|
| `MemControl.cs` | Program, Area, Log, Settings, Snapshot, Native, Cleaner, Tray, MainWindow, Bar, Graph, GraphPoint |
| `Smart.cs` | ProcInfo, Sys, ProgramList, CleanOptions, Activity, AutoPolicy, CommitState, PageFile |
| `AdminTask.cs` | AdminTask (užduotis ir kopija Program Files), AsUser (failų darbas paprasto naudotojo teisėmis), Shared (vardiniai objektai) |
| `Health.cs` | Counters (PDH), Gpu (D3DKMT), PowerInfo, HealthRow, Health, HealthList, HealthWindow |
| `Leaks.cs` | HistoryPoint, HistoryFile, Forecast, Growth, PoolTag, TagRow, KernelTags, LeakWindow |
| `Processes.cs` | ProcessTools, ProcessWindow |
| `KernelHelp.cs` | KernelHelp, TextWindow, CommitHelp |
| `build.cmd`, `install.ps1` | Build į `MemControl.exe` šalia šaltinių; Desktop ir Startup nuorodos |
| `tests/ProcessToolsTest.cs`, `tests/SmartTest.cs`, `tests/run-tests.cmd` | Testai, exit 0 = visi praėjo |
| `docs/*.png` | README paveikslai su pavyzdiniais skaičiais (`SmartTest.exe /shots FOLDER demo`) |
| `memcontrol.ini`, `memcontrol.log`, `memcontrol-history.csv` | Nustatymai, žurnalas, 30 d. istorija šalia exe (gitignore) |

## Komandos

- Build: `build.cmd`. Testai: `tests\run-tests.cmd`. Įdiegimas: `powershell -ExecutionPolicy Bypass -File install.ps1`.
- Programa: `/show`, `/processes`, `/clean`, `/status` (JSON, exit 0/1/2), `/takeover`, `/home FOLDER`, `/admintask create SID HOME|remove`, `/instance NAME` (testams).
- `tests\SmartTest.exe`: `/admin-test MEMCONTROL.EXE` (vienas UAC, tikros teisės), `/shots FOLDER [demo]` (langai į PNG už ekrano),
  `/auto FOLDER` (vienas švelnus automatinis valymas), `/task create NAME EXE ARGS`, `/task delete NAME`.

## Įėjimo taškai ir kopijų bendravimas

- `Program.Main` `MemControl.cs:28`; `Program.Folder` = `/home` arba exe aplankas; `Program.Version`.
- Vardiniai objektai (`Local\MemControl…` + `Program.Instance`): mutex ir įvykiai `Show`, `Processes`, `Quit`, visi per `Shared` `AdminTask.cs:300`.
- `/takeover`: nauja kopija nustato `Quit`, laukia mutex iki 15 s, tada `quit.Reset()`. Perdavimas: `HandOver` `MemControl.cs:86`.
- `Status` `MemControl.cs:108`: `/status` JSON.
- `Tray` `MemControl.cs:503`: 2 s `Update` `:578`; kas 10 s `Sample` `:607`; kas 60 s `Growth.Sample` ir `Tags.Sample`; kas 5 min `LongSample` `:630`.

## „Always (no prompt)" (AdminTask.cs)

- `Install` `:144`: kopijuoja exe į `%ProgramFiles%\MemControl<Instance>\` ir kuria užduotį (`HighestAvailable`, be trigerio,
  `/takeover /home "<duomenų aplankas>"`). `Uninstall` `:158`. `State` `:88`: Ready, Outdated (kopija senesnė ar iš kito aplanko),
  OtherCopy (užduotis rodo kitur), Missing.
- `AsUser` `:237`: pakelta kopija `Log`, `Settings`, `HistoryFile` failus skaito ir rašo per apribotą tokeną
  (`CreateRestrictedToken` LUA, vidutinis vientisumas); rašymas praleidžiamas, jei tokeno sukurti nepavyko.
- `Tray.RestartAsAdmin` `MemControl.cs:769`, `SetAlwaysAdmin` `:798`.

## Valymas (MemControl.cs, Smart.cs)

- `Cleaner.Clean(areas, options)` `MemControl.cs:397`; `TrimProcesses` `:487` po vieną procesą, kai yra ką palikti.
- `Tray.AutoClean` `:656` su `AutoPolicy` `Smart.cs:309`; `Tray.Options` `:701`; `Tray.CommitWatch` `:681`; `PageFile` `Smart.cs:360`.
- `Sys.Processes` `Smart.cs:51` (NtQuerySystemInformation 5; poslinkiai faile), `Rates` `:82`, `CommandLine` `:114`,
  `ListeningPorts` `:137`, `KernelModules` `:193`, `HungPids`, `BootTime`, `FullScreen`.

## „Kodėl lėta?" (Health.cs)

- `Counters` `:16`: PDH pagal angliškus vardus; `Collections >= 2` reiškia, kad greičiai jau galioja.
- `Gpu.Adapters` `:97`: D3DKMT tipas 62 (temperatūra @56, dešimtosios °C) ir tipas 3 (skirta atmintis @0).
- `Health.Collect` `:283`: eilutės Processor, Processor speed, Temperature, Graphics, Disk, Disk space, Memory, Reserved memory,
  Windows kernel memory, Power, Drivers, Windows session, Startup programs, Not responding. Ribos surašytos prie kiekvienos.
- `HealthWindow` `:541`: `Prepare()` nuskaito ir užpildo, `SetRows()` rodo duotas eilutes (naudojama paveikslams).

## Nuotėkių sekimas (Leaks.cs)

- `HistoryFile` `:24`: `memcontrol-history.csv`, eilutė kas 5 min, 8 640 eilučių. `Forecast.KernelGbPerDay` `:95`.
- `Growth` `:127`: 240 mėginių po 1 min; `Steady` `:171` (penkių atkarpų medianos nekrenta); `Growing` `:201` su slenksčiais.
- `KernelTags` `:238`: `Read` `:250` (visos pool žymos), `Rows`, `Known` (Windows žymos), `FindOwners` `:370` (foninė gija skenuoja draiverių failus).
- `LeakWindow` `:427`: `Fill()`, `SetRows()`.

## Langai

- `MainWindow` `MemControl.cs:891` (760×680), `Refresh` `:1057`; `Graph` `:1137` su trimis rėžiais (1 h iš atminties, 24 h ir 7 d. iš `LongHistory`).
- `ProcessWindow` `Processes.cs:110`, `Read` `:224` per `Sys.Processes` (CPU ir sisteminiams procesams), stulpeliai Handles, I/O, Ports, Command line.

## Taisyklės

- Tik C# 5 sintaksė (Windows įmontuotas `csc.exe`): be `$""`, `?.`, `out var`, `nameof`, `=>` nariuose. `dynamic` leidžiamas.
- Naujas `.cs` failas arba nauja `/r:` turi atsirasti ir `build.cmd`, ir `tests/run-tests.cmd`.
- Programos tekstai ir README anglų kalba.
- Saugomi Windows procesai niekada neužbaigiami. Viskas, kas ką nors uždaro ar perkrauna, pirma klausia.
- Testai negali liesti savininko programų ir negali rodyti langų: langus piešti už ekrano (`/shots`), valymą bandyti tik su savo procesais.
- Pakelta kopija nieko nerašo į naudotojo rašomą aplanką savo teisėmis: tik per `AsUser`. Nuorodas atidaryti per `explorer.exe`.
- Į repo nedėti šio PC programų sąrašų ar tikrų langų nuotraukų: tik `demo` paveikslai.
