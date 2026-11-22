# 2026-11-22-001 — ledger bootstrap
Status: done
Requested by user: (task-ledger įgūdis, pirmas naudojimas šiame repo)

## What changed
| File | Lines | Change |
|---|---|---|
| `.ledger/CODEMAP.md` | visas | repo žemėlapis su `path:line` |
| `.ledger/STATE.md` | visas | dabartinė būsena |
| `.ledger/WORKLOG.md` | visas | užduočių rodyklė |
| `.ledger/hashes.json` | visas | CODEMAP failų sha256 |
| `CLAUDE.md` | visas | blokas „Work ledger" |

## Why
- Repo mažas (8 failai, apie 1 370 eilučių), perskaitytas visas vieną kartą.

## Evidence
- Commit: šakoje `claude/smart-cleaning`, tame pačiame commit kaip 1 paketas (užduotis 003).
- `git ls-files` prieš darbą: `.gitignore`, `KernelHelp.cs`, `LICENSE`, `MemControl.cs`, `Processes.cs`, `README.md`, `build.cmd`, `install.ps1`, `tests/ProcessToolsTest.cs`, `tests/run-tests.cmd`.
- HEAD prieš darbą: `0a39d39`.

## Not done / risks
- Build ir testai šioje užduotyje nepaleisti (kodas nekeistas): NOT VERIFIED šiandien.
