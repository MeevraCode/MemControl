# STATE — MemControl (2026-11-22, po 1 paketo)

## Kas yra dabar
- 1 paketas (`dff5c76`) sujungtas į `main` (fast-forward); į `origin` (github.com/MeevraCode/MemControl) nestumta.
  Šaka `claude/smart-cleaning` palikta.
- `MemControl.exe` sukompiliuotas iš naujo kodo (2026-11-22 21:54:34). Senasis: `MemControl.before-smart.old.exe`.
- `build.cmd` exit 0; `tests\run-tests.cmd` exit 0 (21 + 65 patikrinimai).
- Programa po build dar nepaleista.

## Kas padaryta 1 pakete
1. „Always (no prompt)": suplanuota užduotis `MemControl` paleidžia programą su administratoriaus teisėmis be UAC kaskart.
2. Automatinis valymas: laukimas ilgėja (5, 15, 30 min), „Gentle" palieka užimtas ir priekinę programą, pauzė pilno ekrano metu,
   sąrašas „Never clean".
3. Commit įspėjimas nuo pasiekiamų lubų (85 % ir 93 %), „Explain" langas, didžiausi turėtojai.

## NEPATIKRINTA (reikia savininko)
- Užduotis su tikromis teisėmis: pažymėti „Always (no prompt)" ir patvirtinti UAC. Tikėtina eiga ir ką žiūrėti:
  `tasks/2026-11-22-003-pamatai.md`, skyrius „Not done / risks".

## Žinoma rizika (automatinė saugumo peržiūra, 2026-11-22)
- „Always (no prompt)" užduotis su administratoriaus teisėmis ir be klausimo paleidžia exe iš aplanko, į kurį gali rašyti
  naudotojo programos; pakeitusi tą failą, bet kuri iš jų gautų tas pačias teises. Funkcija pagal nutylėjimą išjungta,
  kaina aprašyta README ir varnelės užuominoje.
- Pasiūlytas sustiprinimas NEDARYTAS: įjungiant kopijuoti exe į `%ProgramFiles%\MemControl` ir užduotį nukreipti į tą kopiją
  (po kiekvieno build reikėtų vieno UAC kopijai atnaujinti). Savininkas dėl jo neatsakė ir paprašė sujungti į `main`.
- Susijusi senesnė silpnybė: pakelta kopija rašo `memcontrol.ini` ir `memcontrol.log` į naudotojo rašomą aplanką.

## Likusios spragos
- Istorija tik 60 min ir tik atmintyje: nėra ilgo grafiko, nuotėkio greičio, prognozės.
- Iš pool žymų lentelės naudojama viena eilutė „File".
- Procesų tvarkyklėje nėra komandinės eilutės, prievadų, handles, I/O; CPU % ten vis dar iš `TotalProcessorTime`
  (dabar yra `Sys.Processes`, kuris duoda CPU ir sisteminiams procesams).

## Laukia savininko sprendimo
Paketai 2–5 iš `tasks/2026-11-22-002-nauju-funkciju-analize.md`: „Kodėl lėta?", nuotėkių sekimas, programuotojui, žaidėjui.
Rekomenduota tvarka: 2, tada 3.

## Bandymų mašina
Laptopas: Ryzen 7 260 (Radeon 780M) ir GeForce RTX 5060 Laptop (8 GB), 32 GB RAM, Windows 11 26200.
