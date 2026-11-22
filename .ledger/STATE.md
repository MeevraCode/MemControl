# STATE — MemControl 1.1.0 (2026-11-22)

## Kas yra dabar
- Versija 1.1.1 (1.1.0 ir saugumo pataisa, žr. užduotį 005). Kodas, testai ir build baigti.
- `build.cmd` exit 0; `tests\run-tests.cmd` exit 0 (141 patikrinimas); `/admin-test` su tikromis teisėmis praėjo su 1.1.0 kodu (15).
- Išleista GitHub: v1.1.0 ir v1.1.1 (github.com/MeevraCode/MemControl); naujausias yra v1.1.1.
- Programa savininko PC po build dar nepaleista. Senasis exe: `MemControl.before-smart.old.exe`.

## Kas yra programoje
1. Valymas: rankinis, automatinis su atsitraukimu, „Gentle", pauzė pilno ekrano metu, „Never clean".
2. „Always (no prompt)": kopija `%ProgramFiles%\MemControl` ir užduotis `MemControl`; failų darbas per apribotą tokeną.
3. Įspėjimai: kernel atmintis (4 ir 8 GB), commit (85 % ir 93 % pasiekiamų lubų).
4. „Why is it slow?": 13–14 eilučių su spalva, skaičiumi ir patarimu.
5. „Leak watch": augančios programos, kernel atmintis pagal savininką, zombiai, prognozė iš istorijos diske.
6. Procesų tvarkyklė: komandinė eilutė, prievadai, handles, I/O; `/status` JSON.

## Nepatikrinta
- Valymas po vieną procesą su administratoriaus teisėmis.
- Ilgalaikiai dalykai su tikrais duomenimis: 24 h ir 7 d. grafikas, prognozė, augančių programų sąrašas.

## Likę sumanymai (nedaryti)
- Žaidėjui: patikra prieš žaidimą, efektyvumo režimas fono programoms.
- Programuotojui: kūrėjo įrankių grupė, „našlaičių" paieška, įspėjimas apie staiga išaugusį procesą.
- README paveikslus perdaryti po kiekvieno lango pakeitimo: `tests\SmartTest.exe /shots FOLDER demo`.

## Video
40 s video padarytas ir prisegtas prie leidimo v1.1.1 (`MemControl-1.1.mp4`); vietinė kopija `video/MemControl-1.1.mp4` (gitignore).
Perdaryti: `node video/make.mjs FOLDER`. Kompiuteryje nėra `ffmpeg`, koduoja Chrome.

## Bandymų mašina
Laptopas: Ryzen 7 260 (Radeon 780M) ir GeForce RTX 5060 Laptop (8 GB), 32 GB RAM, Windows 11 26200.
