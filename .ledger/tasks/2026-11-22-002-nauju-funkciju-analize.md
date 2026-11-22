# 2026-11-22-002 — naujų funkcijų analizė ir matavimų bandymas
Status: done
Requested by user: „ka galima dadeti, kad butu kuo issamiau zmogui? naudingu funkciju, kurios tikrai veiktu?? … isanalizuok koda"

## What changed
| File | Lines | Change |
|---|---|---|
| programos kodas | – | nekeistas |
| `.ledger/evidence/2026-11-22-probe.cs` | visas | tik skaitantis bandymas: kurie matavimai veikia šiame PC |
| `.ledger/evidence/2026-11-22-noop.cs` | visas | tuščia programa nuotėkio savitestui |

## Patikrinti duomenų šaltiniai (Windows 11 26200 x64, be administratoriaus teisių)
| Kas | API | Rezultatas |
|---|---|---|
| Visi procesai vienu kvietimu | `NtQuerySystemInformation(5)`. Poslinkiai x64: Next@0, Threads@4, PrivateWS@8, HardFaults@16, Create@32, User@40, Kernel@48, NameLen@56, NamePtr@64, Pid@80, Ppid@88, Handles@96, WS@144, PagedPool@160, NonpagedPool@176, PrivateBytes@200, ReadBytes@232, WriteBytes@240 | veikia; sutampa su .NET `Process`; duoda CPU ir I/O ir sisteminiams procesams |
| Komandinė eilutė | `NtQueryInformationProcess(60)` su `PROCESS_QUERY_LIMITED_INFORMATION` (0x1000) | perskaityta 34 iš 34 savo naudotojo procesų |
| Klausomi TCP prievadai su PID | `GetExtendedTcpTable`, klasė 3; AF_INET eilutė 24 B (port@8, pid@20), AF_INET6 eilutė 56 B (port@20, pid@52) | veikia, 59 prievadai |
| GDI ir USER objektai | `GetGuiResources` su 0x1000 | veikia |
| Visos pool žymos su baitais | `NtQuerySystemInformation(22)`, baitai @16 ir @32 | 3 253 žymos |
| Zombiai | žymų „Proc" ir „Thre" gyvi objektai minus veikiantys procesai ir gijos | veikia (465 prieš 367; 6 868 prieš 6 193) |
| Žyma → draiveris | `NtQuerySystemInformation(11)` (įrašas 296 B, kelias @40) ir modulių failų skenavimas | 245 moduliai; 242 failai, 216 MB per 7,5 s, todėl reikia kešo. NVRM → nvlddmkm.sys, FMfn → FLTMGR.SYS, NtfF → Ntfs.sys; „Toke", „HalD" per dažni |
| CPU greitis ir ribojimas, DPC, temperatūra, diskas, puslapiavimas, standby, page file, GPU apkrova ir atmintis | PDH: `PdhAddEnglishCounterW`, `PdhGetFormattedCounterArrayW` (elementas 24 B, formatas 0x200). Keliai surašyti `probe.cs` masyve `CounterPaths` | visi 24 skaitikliai veikia; šiluminė zona `\_TZ.THRM` yra |
| Atminties sąrašai | `NtQuerySystemInformation(80)`, 176 B: zero@0, free@8, modified@16, standby 8 prioritetai nuo @40 | veikia ir be teisių |
| GPU temperatūra | `D3DKMTEnumAdapters2` (adapterio įrašas 20 B), `D3DKMTQueryAdapterInfo` tipas 62, 64 B, Temperature@56 dešimtosiomis °C | NVIDIA veikia; AMD 780M grąžina 0xC000000D |
| VRAM dydis | registras `Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\000x\HardwareInformation.qwMemorySize` | veikia |
| Maitinimas | `GetSystemPowerStatus`, `PowerGetActiveScheme` su `PowerReadFriendlyName`, `PowerReadACValueIndex`/`PowerReadDCValueIndex` (PROCTHROTTLEMAX), `PowerGetEffectiveOverlayScheme` | veikia. `CallNtPowerInformation(11)` CurrentMhz visada rodo nominalą, nenaudoti |
| Pilno ekrano būsena | `SHQueryUserNotificationState` | veikia: 2, kai žaidimas priekyje |
| Fast Startup, laukiantis perkrovimas, startup įrašai | registras (HiberbootEnabled, RebootRequired, Run, StartupApproved) | skaitymas veikia |
| Kernel paleidimo laikas | `NtQuerySystemInformation(3)`, BootTime@0 | veikia |
| Nuotėkio savitestas (200 procesų startų) | „File" žymos pokytis | NEPATIKIMAS: per 200 startų −1 309 objektai, ramybėje +53 ir +49 per 5 s; triukšmas didesnis už laukiamą signalą (+400) |

## Nepatikrinta (keičia sistemos būseną, bandyme neliesta)
- Paleidimas su teisėmis per Task Scheduler. Žinomi spąstai: numatytieji „tik prijungus prie tinklo", „sustabdyti perėjus į bateriją",
  72 h vykdymo riba ir prioritetas 7; `schtasks` eilutė jų nekeičia, reikia XML arba `Register-ScheduledTask` su nustatymais.
- Efektyvumo režimas: `SetProcessInformation(ProcessPowerThrottling)` ir `SetPriorityClass`.
- Startup įrašų išjungimas per `StartupApproved` (pirmas baitas 02 įjungta, 03 išjungta).
- `/status` išvestis iš `winexe` (reikės `AttachConsole` arba rašyti tik į nukreiptą stdout).
- Working sets tuštinimas po vieną procesą su `SeDebugPrivilege` (reikalingas išimtims; dabar su teisėmis tuštinama visiems viena komanda, `MemControl.cs:296`).

## Pasiūlyti paketai (savininkas dar nepasirinko)
1. Pamatai: paleidimas su teisėmis be UAC kaskart; automatinis valymas su atsitraukimu, tik neaktyvioms programoms, pauzė pilno ekrano
   metu, išimčių sąrašas; commit įspėjimas su didžiausiais commit turėtojais.
2. „Kodėl lėta?": CPU greitis ir ribojimas, temperatūros, GPU ir VRAM, diskas ir laisva vieta, puslapiavimas, maitinimo planas,
   DPC, sesijos amžius ir Fast Startup, startup programos, RAM sudėtis (naudojama, suspausta, modified, standby, laisva).
3. Nuotėkių sekimas: procesų tendencijos (atmintis, handles, gijos, GDI/USER), kernel žymų augimas su draiverio vardu, zombiai,
   istorija diske (24 h ir 7 d. grafikas, greitis MB/h, prognozė iki ribos).
4. Programuotojui: komandinės eilutės stulpelis ir kūrėjo įrankių grupė, prievadai su „užbaigti", našlaičiai, `/status` su exit kodu.
5. Žaidėjui: patikra prieš žaidimą (kas fone nereikalinga), efektyvumo režimas fono programoms žaidimo metu.

## Why
- Siūloma tik tai, kam duomenų šaltinis patikrintas šiame PC; kas nepatikrinta, pažymėta atskirai.
- 1 paketas pirmas, nes žurnalas rodo realią naudą: 130 iš 144 automatinių valymų vyko be teisių, 94 iš 144 per 2,5 min nuo ankstesnio.
- Greitas nuotėkio savitestas atmestas kaip nepatikimas užimtoje sistemoje.

## Evidence
- Commit: šakoje `claude/smart-cleaning`, tame pačiame commit kaip 1 paketas (užduotis 003).
- Bandymas sukompiliuotas tuo pačiu `csc.exe` kaip `build.cmd` (`/target:exe`), paleistas be teisių, 2026-11-22 apie 21:05.
- Išvesties ištraukos: `self-check against .NET: private 21 MB vs 21 MB | handles 355 vs 357`;
  `\_TZ.THRM = 67.9 C`, `% Passive Limit = 100`; `tags: 3253`; `process objects alive in the kernel: 465 | running processes: 367`;
  `scanned 242 files, 216 MB in 7.5 s`; `adapter 0: status 0x00000000 | temperature 72.5 C`, `adapter 1: status 0xC000000D`;
  `zero 383 MB, free 6 MB, modified 73 MB, standby 3,957 MB`; `command line readable for 34 of 34`; `notification state: 2`.
- `memcontrol.log` (2026-11-01 – 2026-11-22, 149 eilutės): `auto: 144 | skipped-without-admin: 130`;
  `within 2.5 min of the previous one: 94`; `freed under 1 GB: 76 of 144`.
- Būsena bandymo metu: RAM 32 013 MB, available 4 345 MB; commit 48 092 iš 51 025 MB; kernel pool 1 039 + 1 031 MB praėjus 1,8 d.
  nuo paleidimo; gyvų „File" objektų 27 868.

## Not done / risks
- Build ir testai nepaleisti (kodas nekeistas).
- Ar draiverio nuotėkis dar vyksta, neaišku: šioje sesijoje jis nesikaupia, bet AMD draiveris tas pats (2025-04-11). Atsakytų tik istorija diske.
- Pilna bandymo išvestis į repo nedėta (joje šio PC programų sąrašai; repo turi nuotolinę saugyklą GitHub).
