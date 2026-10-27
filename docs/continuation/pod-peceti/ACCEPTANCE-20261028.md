# Pod pečetí — implementace a akceptace 28. 10. 2026

Tento záznam patří práci v jediném pokračovacím chatu nad HEAD
`71b0349bac4f5b8fb9d195252339474735c6d7b6`. Není souhlasem k publikaci.
Historické Hermes reporty zůstávají historickými důkazy. Board ani časovač
pokračování nebyly obnoveny. Žádný commit, push, merge ani deploy nebyl proveden.

## Dodané funkční celky

- C1: frozen postava a portrét, explicitní scénářový souhlas, 8–30 míst,
  restartovatelný privacy fence/erase/ack a explicitní legacy reselect.
- C2–D2: skutečný start, serverová časová osa 3/6 kapitol, pause/resume,
  poslední výslovně potvrzený plán, vlastní revize, HOLD, offline návrh a opakování
  stejného potvrzení i po obnovení stránky. Global case revize není součástí
  hráčova potvrzení. Create rovněž uchovává klíč při ztracené odpovědi.
- E1–G2: verzovaný serverový scénář, pět zón, pohyb, podmínky, příprava,
  obrana, spánek, útok/pomoc, omezená sabotáž, maska, sledování, společný střet
  o jediný předmět, předání, sny a Ozvěny. Původní N/T/K se nemění odchodem.
  Krytí vyžaduje výslovně dosažitelnou pozici a lokaci s krytem. Pasivní
  reakce nepřidává útok ani ochranný bonus; ústup může použít dosud nevyužitý
  sousední přesun až po společných kontaktech.
- G1/H1/H2: A/B/C důkazní řetězce s párovou nejednoznačností a jednoznačnou
  trojicí, omezený přísun nových faktů, vlastní oprávněné stopy, tajné hlasy,
  abstence, druhé kolo a seedovaný rozstřel až po uzávěrce. Pravda a rekonstrukce
  čekají na konec finále a uložení všech kapitol.
- I1/I2: šest původních promptů je serverovým embedded obsahem. Role dostávají
  omezené projekce; read-only AI adaptér používá bounded transport, strict JSON,
  timeout, zdrojové reference, tokeny/latenci a český fallback. Běžný start aplikace
  nemá registrovaného placeného providera. Hra zobrazuje faktické šablony; polling
  ani založení případu nevolají API. Placené testy jsou samostatný syntetický harness.
- J1/J2: Příběh / Moje akce / Deník / Rozhovory v existujícím shellu. Tutorial
  nabízí právě tři návrhy; full umožňuje vlastní popis s jedním strukturovaným
  úmyslem. Soukromé zprávy mají omezené publikum. Limit zpráv se počítá až po
  autorizaci publika, takže cizí soukromý provoz nevytlačí vlastní konverzaci.
- K1/K2: zůstávají existující komponenty, fonty, barvy a portréty cleanup
  `c91166ce9adab0eea4ced9e40e4a731548f05b15`. Mockupy se neportovaly. Nové ilustrace,
  rámy, pečetě ani zvuky ze ZIPu se nepřidávaly: nejsou nutné pro funkčnost a
  měnily by existující výraz. Žádné autoplay ani nové pohybové efekty.
- L1/L2: rekonstrukce uložených akcí/následků včetně hodů, modifikátorů a příčin.
  Před finále jsou tyto skryté detaily ve vlastním přehledu odstraněny.
  Medailonek odvozený z uložených vlastních plánů a skutečných výsledků, výslovný lokální export veřejné rekonstrukce nebo vlastní vzpomínky;
  soukromé rozhovory se do žádného z těchto exportů nevkládají.
- M1: host řídí pauzu a eviduje odpojení. Soutěžící se přes administrační roli
  nedostane k pravdě světa; moderátorský endpoint vyžaduje nesoutěžícího
  oprávněného administrátora stejné skupiny. Dohled nemění hlavní tah, neodhaluje
  tým a při rizikovém fyzickém zásahu zanechává zdrojovanou stopu místním svědkům.

## Nově provedené ověření

Plné výstupy a syntetické AI záznamy jsou v ignorovaném
`backend-tests/bin/pod-peceti-completion/`. Neobsahují produkční hosty ani API klíč.

| Ověření | Výsledek a rozsah |
| --- | --- |
| Release backend build | PASS, 0 warnings/errors |
| `--sealed-case-runtime` | PASS: pravidla, projekce, persistence, restart/privacy, časování, plány a HTTPS účty/CSRF/skupiny |
| HTTPS playable | PASS: tutorial 8 klientů / 3 kapitoly a full 30 klientů / 6 kapitol, souběžné finále, hlasy a rekonstrukce |
| Cílené mechaniky | PASS: explicitní krytí Archiv/Salon, pasivní ústup, dohled a jediný vlastník při souběžné krádeži/předání |
| Frontend build | PASS; stávající upozornění Vite na velikost chunku |
| Browser | PASS: 320×844 a 390×844, výslovné návrhy, lost-ack akce/create a reload, deník, klidový úsek, klávesnicový focus, text 200 %, reduced-motion, render medailonku a stažený export bez soukromého chat canary, bez page errors |
| Server-only asset kontrola | PASS, 2 testy nad skutečným dist |
| Nezávislé review | Přijato bez otevřených critical/high/medium; receipt v tomto chatu |
| AI boundary a account deletion | PASS v cíleném novém buildu: šest rolí, odmítnuté zdrojové reference, fallback, saga a HTTPS výmaz |
| Nativní iOS | NOT RUN — zde není macOS/Xcode ani fyzické zařízení |
| Lidský pilot | NOT RUN — AI simulace jej nenahrazuje |

Při prvním souběžném finále nastalo HTTP 503. Offline souběžná regrese jej
reprodukovala; WAL odstranil cyklus čtení case / identity reservation / writer
commit. Následně prošlo souběžné finále 8 i 30 klientů. Soubor `runtime-complete.log`
je zelený souhrn; později přidané boundary testy mají vlastní nový build/log.
Starší neúspěšné logy se nepřepisují ani nevydávají za úspěch.
Finální cílený build a `playable-ai-transport.log` obsahují také nový AI boundary
PASS; `account-deletion.log` dokládá nový průchod sagou/HTTPS. Finální frontend je
v `frontend-medallion.log`, skutečný render/export v `browser-export-final.log`
a poslední kontrola jeho dist v `assets-final.log`. Malé navazující změny adresy
bounded transportu a odvozeného medailonku byly po integračním review ověřeny
cílenými testy; nejde o tvrzení, že původní reviewer fingerprint zahrnuje jiné bajty.

### Autorizované AI partie

Použit byl uživatelem vybraný existující klíč pouze v paměti procesu, model
`gpt-5-mini`, osm oddělených hráčských projekcí, syntetické účty. Žádná produkční
AI. Limit je nejvýše deset partií, každá se započítá před prvním požadavkem;
ledger a procesní zámek brání paralelnímu vyčerpávání stejného limitu.

| Pokus | Varianta | Výsledek | Dokončené kapitoly | API požadavky |
| --- | --- | --- | --- | --- |
| 1 | První případ, 8 hráčů | FAILED při souběžném finále; započten | 3/3 | 32 |
| 2 | První případ, 8 hráčů | PASS včetně finále | 3/3 | 32 |
| 3 | Plná hra, 8 hráčů | PASS včetně finále | 6/6 | 56 |
| 4 | První případ, nový bounded transport | PASS včetně finále | 3/3 | 32 |

Neúspěšný pokus se neodečítá. Nejde o důkaz zábavnosti nebo vyváženosti;
ve všech třech úspěšných simulacích vyhráli Spiklenci. Započteno 4/10 partií; další placené testy nejsou naplánovány.

## Lokální provozní příručka a obnova (M2)

1. Použít přidělený checkout, ověřit HEAD, pracovní diff a runtime verzi obsahu.
   Feature vyžaduje existující account identity a explicitní `SealedCase:Enabled`.
   Zapnutí či změna produkční konfigurace není součástí této práce.
2. Oprávněný host založí případ v `/play/pod-peceti`. Hráči stejné skupiny
   připojí svou uloženou postavu, existující portrét a přijmou scénářový souhlas.
   Před startem je nutná úplná kapacita; pozdější odchod nemění původní počty.
3. Po startu server odvozuje fáze z hodin. GET obnoví oprávněnou projekci a
   dožene dosud neuložené uzávěrky. Opakovat stejný operationId; neposílat nový
   tah kvůli ztracené odpovědi. Při více než čtvrtině odpojených host vyhlásí pauzu.
4. Diagnostika: evidence event ID, case ID, fáze, kód chyby, HTTP status a
   runtime/content verze. Nezapisovat profily, soukromé zprávy, celé kontexty,
   tokeny, cookie ani tajný seed do sdílených logů.
5. Konzistentní zálohu pořizovat při zastavených zápisech. Zálohovat společně
   identity, character/privacy a case databázi a potřebné soubory portrétů se
   shodným časovým bodem a verzí aplikace. Pro SQLite použít Backup API;
   samotná kopie `.sqlite` za provozu nestačí kvůli WAL. Zálohu držet soukromě.
6. Obnovu nejprve ověřit v izolovaném datovém adresáři se zakázanou AI a bez
   veřejného ingressu. Spustit reconcile pending privacy/delete sag, ověřit
   terminal deny, frozen resolver, plán/replay a časování před otevřením přístupu.
   Obnova staré zálohy nesmí obejít novější požadavek na výmaz; privacy tombstones
   je nutné zachovat a aplikovat. Starší samostatnou case databázi nepřipojovat
   k novějším identitám jako náhradu konzistentní obnovy.
7. Schéma se doplňuje stávající idempotentní inicializací; checkpoint pole jsou
   aditivní a null optional command pole zachovávají legacy fingerprint.
   Downgrade vyžaduje původní zálohu a odpovídající binární verzi. Produkční
   rollback, push, merge i deploy mají samostatné schválení.

WAL předpokládá lokální disk jednoho hostitele. Podrobnosti souběhu a záloh:
[SQLite WAL](https://www.sqlite.org/wal.html) a
[SQLite Online Backup API](https://www.sqlite.org/backup.html).

## Pilotní postup (N5)

- Osm dospělých dobrovolníků, testovací večer a postavy, explicitní souhlasy.
  Nejdříve 45minutový První případ, potom případně plná hra. V produkci zatím nenasazeno.
- Každý jednou rozešle tvrzení, odešle a upraví plán; dva hráči zkusí ztrátu
  připojení a návrat. Host předvede pauzu. Pozorovatel ověřuje, že cizí soukromý
  stav ani skrytá smrt nejsou prozrazené nabídkou nebo potvrzením.
- Po finále každý porovná vlastní deník s rekonstrukcí. Vzpomínku exportuje jen
  výslovně a zkontroluje její soukromý obsah před jakýmkoli sdílením.
- Zaznamenat srozumitelnost stopy/příkazu, délku čekání, prostor pro oba týmy,
  zábavnost, použití Ozvěn, chyby/úniky a ochotu hrát znovu. Zvlášť sledovat,
  zda přísun faktů dovoluje Strážcům reálné vítězství.
- Výsledek lidského pilotu nyní **NOT RUN**. Publikaci a produkční připravenost
  neodvozovat ze samotného buildu, browser screenshotu ani AI partie.
