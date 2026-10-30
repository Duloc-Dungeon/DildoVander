# Kouzlo bez dozoru

Hratelná arkáda WNS na `/play/kouzlo-bez-dozoru`, dostupná z nabídky her.
V existujícím žebříčku má samostatnou disciplínu (`/play/ranking#spark`).
Jiskra letí sama; dotyk obrátí boční směr. Jedna celá překonaná brána znamená
jeden bod, náraz pokus ukončí. Skupinový rekord je maximum jednoho ověřeného
pokusu. Hra nepřidává bojová XP ani YOLO výhry.

V přehledu `/play` jsou tři samostatné ilustrované vstupy: nejprve Souboj hůlek,
pak Kouzlo bez dozoru a Pod pečetí. Souboj hůlek na `/play/souboj-hulek` sdružuje
1v1, odemčené taktické a YOLO režimy, týmovou hru, trénink a odhaleného Barona.
Podmínky odemykání se nemění. Arkáda je označená jako hra pro jednoho,
Pod pečetí jako příběhová hra pro skupinu. Před vytvořením hůlky je arkáda skrytá
a vstup do Souboje hůlek vede na přípravu hůlky; Pod pečetí zůstává dostupné.
Úvod, pauza a výsledek arkády používají společné tmavé povrchy, Manrope
a mintová tlačítka WNS; původní herní ilustrace a pravidla zůstávají zachované.

## Rešerše a rozhodnutí

Zdroje ověřené 30. října 2026. Uživatelská rešerše motivace je návrhový podklad,
nikoli důkaz retence této konkrétní implementace.

| Podklad | Rozhodnutí v implementaci |
| --- | --- |
| [Glenn Fiedler: Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/) | Simulace běží v pevných celočíselných krocích 60 Hz. Vykreslení ani obnovovací frekvence nemění fyziku. Prodleva přes 250 ms vyvolá pauzu. |
| [MDN: touch-action](https://developer.mozilla.org/en-US/docs/Web/CSS/Reference/Properties/touch-action) a [pointercancel](https://developer.mozilla.org/en-US/docs/Web/API/Element/pointercancel_event) | `pointerdown`, zachycení primárního ukazatele, `touch-action:none` jen na hrací ploše, přerušený dotyk pozastaví pokus. |
| [MDN: Page Visibility](https://developer.mozilla.org/en-US/docs/Web/API/Page_Visibility_API) | Skrytí aplikace zastaví čas; návrat vyžaduje vědomé pokračování. Síť neřídí herní smyčku. |
| [PixiJS Application](https://pixijs.com/8.x/guides/components/application) | PixiJS 8.22.0 vykresluje texturu jiskry, průlety, stopu a zvýraznění nárazu. Rozlišení má strop 2×. Simulace nepotřebuje PixiJS. |
| [Oficiální PixiJS: prostředí s CSP](https://github.com/pixijs/pixijs/blob/dev/skills/pixijs-environments/SKILL.md) | Import `pixi.js/unsafe-eval` nahrazuje dynamické generování kódu statickými funkcemi. Stávající CSP zůstává přísná. Název importu neznamená povolení eval. |
| Zadání humoru a rozšířená typologie dodané uživatelem pro tento projekt; pravidla v `docs/humor-style.md` | Původní české situační hlášky: deadpan, antiklimax, doslovnost a absurdní důsledky. Liší se náraz do stěny, police, úzký průlet a rekord. Pointa nemění pravdivý stav. |

PixiJS je prezentační vrstva; plný fyzikální engine by zde nepřinesl lepší
pravidlo ani spolehlivější replay. Kruh a dvě obdélníkové police mají přímo
ověřitelnou geometrii. Světlé jádro odpovídá koliznímu kruhu, záře neškodí.
Zvuk je volitelný a používá krátké lokálně syntetizované tóny; haptika je opt-in.
Při omezeném pohybu se vypíná dekorativní stopa a kruhový efekt, pohyb nutný
pro samotnou hru zůstává. Klávesnice: mezerník/Enter, pauza Escape.

## Obtížnost a férovost

- První široký průlet dovoluje zažít bod brzy. Další vyžadují změnu směru.
- Posunuté otvory a dvojice navazujících bran vyžadují přípravu trajektorie.
  Každá sedmá brána je delší průlet. Mezery se pozvolna zužují až k pevnému minimu.
- Rychlost se nezvyšuje. Jiskra je ve třech čtvrtinách logické výšky a nad ní
  jsou vidět další překážky. Obrazovka nemění rozměry fyziky.
- Celé navazující úseky ověřuje průchod všech dosažitelných stavů, nikoli jen
  samostatná šířka bran. Test prvních 300 bran běží na sedmi semenech s možností
  rozhodnutí jednou za 200 ms. Dvě další trajektorie procházejí celých 30 minut.
- Testy pravidelného rychlého klepání na různých frekvencích a semenech
  nedosahují vysokých skóre. Jde o omezenou sadu testovaných strategií, nikoli
  důkaz proti libovolnému botovi.
- Po nárazu zůstane 360 ms původní scéna s označeným kontaktem. Nový pokus
  spouští samostatné tlačítko; předchozí dotyk se nepřenáší do restartu.
- Bezpečnostní strop jednoho pokusu je 108000 kroků / 30 minut / 1200 bran.
  Přeživší pokus je dokončený průlet skladu, nikoli prohra.

## Výsledek a soukromí

Server odvodí účet a aktivní skupinu z existujícího oprávnění. Před startem vydá
ticket se semenem společným pro skupinu, verzí pravidel a dvouhodinovou platností.
Klient posílá pouze poslední krok a časy klepnutí; server nezávisle přehraje
geometrii v C#, ověří první náraz a atomicky uloží maximum a idempotentní účtenku.
Libovolné číslo skóre se nepřijímá. Opakování HTTP požadavku nepřidává body.
Replay ověřuje proveditelnost, ne lidský původ vstupů; není ochranou proti botům.

Vstupy mají limit 24000, HTTP tělo 512 KiB, účet nejvýše 64 rozpracovaných
neexpirovaných pokusů. Samostatný limiter dovoluje 180 zápisů a 120 čtení za
minutu; ostatní herní limity se nemění. Expirující tickets/receipts se čistí při
vydávání dalších. Výmaz účtu maže pokusy i rekordy a zachovává existující fence
proti zápisu po výmazu. V další verzi fyziky musí být rekordy odděleny nebo
migrovány; dnešní disciplína používá pouze `spark-v1`.

Síťová chyba ponechá záznam v prohlížeči pod klíčem příslušného účtu; opakované
odeslání používá stejný pokus. Fronta blokuje další soutěžní start při pěti
čekajících výsledcích, trénink zůstává dostupný. Fronta není zárukou přežití
smazání dat prohlížeče nebo zablokovaného úložiště. Trénink má samostatný lokální
rekord a jednotnou vlastní trať; nikdy se nepřevléká za ověřené skupinové skóre.

Přesná pravidla a API: [arcade-rules-v1.md](arcade-rules-v1.md),
`contracts/game-openapi-v1.json`. Vlastníci implementace:
`mobilni-aplikace/src/features/arcade/`, `backend/Features/Game/GameArcade.cs`.

## Grafika a licence

Dva originální rasterové assety vygeneroval vestavěný OpenAI image_gen:
kouzelnický sklad a průhledná jiskra. Zdrojové PNG, produkční WebP a kompletní
prompty jsou v `mobilni-aplikace/src/assets/arcade/` a `manifest.json`.
Produkční obrázky dohromady zabírají přibližně 129 kB. Fyzika ani průhlednost
kolizí nejsou odvozené z dekorativních obrázků.

`mobilni-aplikace/scripts/prepare-spark-assets.mjs <cesta-k-sharp>` reprodukuje
WebP ze zdrojových PNG; `sharp` je pouze nástroj přípravy, nikoli runtime závislost.
PixiJS a přibalené závislosti mají původní licenční texty zkopírované buildem do
`third-party-notices/pixi/`. `@pixi/colord` licenci do npm tarballu nepřibalil;
lokální kopie je z [oficiálního forku](https://github.com/pixijs/colord/blob/master/LICENSE.md).

## Opakování ověření

Z kořene produktu:

```powershell
npm ci --prefix contracts
npm ci --prefix mobilni-aplikace
node mobilni-aplikace/scripts/generate-spark-vectors.mjs
npm test --prefix contracts
npm run build --prefix mobilni-aplikace
npm run test:spark:browser --prefix mobilni-aplikace
dotnet run --project backend-tests -- --mobile-game-arcade
dotnet run --project backend-tests -- --mobile-game-arcade-http
```

Frontend testy: `npm test` z `mobilni-aplikace`. Hra používá stávající lokální
fixture server, dostupný přes `npm run preview:game`, cesta `/__game/spark`.
Náhled má syntetické účty a výsledky; nepřipojuje se k živým poskytovatelům.

Ověřeno v místním prostředí Windows: shoda obou simulací včetně dvou plných
30minutových replayů, ukládání/identity/ties/expiry/výmaz, skutečné HTTP přijetí
validního záznamu nad 64 KiB, odmítnutí bez CSRF a nad 512 KiB, frontend fronta
a závody odpovědí, produkční build a browser na 320×568, 390×844, 1280×900.
Browser scénář ověřuje ovládání, přerušený dotyk, syntetický visibility event,
restart, pauzu, výpadek uložení, reload/retry, žebříček, reduced motion a 200% text.
17 snímků je ukládáno mimo zdroj přes `APP_SCREENSHOTS` nebo do systémového temp.

Neověřeno: skutečný telefon/iOS/Android, reálná latence dotyku a haptika,
živá nasazená služba, lidská zábavnost a retence. Doporučený další produktový
krok je pozorovaný playtest s 8–12 hosty: pojmenování chyby, změna načasování,
dobrovolný další pokus. Průchodnost solveru není důkaz lidské zábavnosti.
