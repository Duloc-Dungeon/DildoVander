# WNS v2 — implementace a skutečně provedené kontroly

## Výchozí stav

- Balíček: edice 2.0, SHA-256 v `../DESIGN.md`; inventura `8c202e5`.
- Pracovní větev `codex/wns-design-v2` vychází z `c5a7ffb` (cleanup).
- Oproti inventuře cleanup odstranil dokumentaci, design runtime a CAD zdroje.
  Herní/auth zdroje zůstaly. Novější pravidla humoru a češtiny jsou zachována.
- První build selhal na odstraněných `theme.mjs`, `ui.js` a capture workletu.
  Obnovené soubory jsou nezbytné runtime závislosti, nikoli nové CAD výstupy.

## Etapy — stav není totéž co kompletní přejímka

| Etapa | Implementovaný rozsah | Zbývá |
| --- | --- | --- |
| A — základ a vstup | Tokeny, místní Manrope, licencované assety bez demo portrétu; vstup/passkey formuláře, tmavé plochy, postava/čekárna a zachovaný revision/server gate. | Úplné porovnání všech variant s cílovými 45 návrhovými ID; finální nezávislá UI kontrola. |
| B — hry | Nový app-only adaptér, jantarová hůlka v hubu, navigace Večer/Postava/Památky/Já; původní rychlý/taktický/týmový/boss/YOLO stav a příkazy; explicitní opt-in zvuku. | Kompletní cílová choreografie a autoritativní event stream pro výsledkové efekty; živá AI/multiplayerová přejímka. |
| C — památky/účet | Sdílená vizuální vrstva a paměťová koule prázdné kolekce; původní owner/link/recovery/export/delete chování se nemění. | Kompletní cílové obrazovky/edice a skutečný nativní export na zařízení. |
| D — Pod pečetí | Serverový rules/projection základ, SQLite journal, revize/idempotence/uzávěrka, plán variant, hlasovací validace a pause/resume; skutečné autorizované HTTP založení/čtení čekárny a připravenost se souhlasem. Focused C# harnessy procházejí. | Propojení úložiště s enginem, kompletní scénářové adaptéry/pravidla, start, UI, finální runoff integrace a plná přejímka tajných projekcí. Start a akce výslovně vracejí 422, funkce je defaultně vypnutá. Není to hotová hra. |
| E — hostitel/provoz | Původní capability a skupinový launch zůstávají; sdílený vzhled. Ověřeno existující oddělení návrhu, potvrzení odpočtu a otevření skupiny v `BetaAdmissionPanel.vue`. | Úplná vizuální shoda cílové hostitelské cesty a mobilní přejímka. Není potřeba znovu vytvářet už existující skupinové API. |

## Ověřené průchody

Prostředí: izolovaný Linux ARM64 VPS, Node 24.21.0, .NET SDK 10.0.303,
Chromium Playwright. Web běží z opravdového produkčního `dist`; browser testy
používají výslovně syntetické loopback API, nikoli produkční účty či AI.

- `npm test --prefix mobilni-aplikace`: 154 Node testů a 1 031 Vitest testů
  prošlo v prvním integračním checkpointu. Není to důkaz pozdějšího finálního diffu.
  Vitest vypsal i zachycené ECONNREFUSED na lokální 3000, exit code zůstal 0.
  Nový plný běh po úpravách jazykové volby a tykání: 155 Node a 1 033 Vitest
  testů prošlo; zachycený ECONNREFUSED na lokální 3000 se stále vypisuje.
  Prošel také celý `npm test --prefix contracts`, nový build a samostatně
  opakovaný browser průchod. Kombinovaný test/build/browser příkaz narazil na
  limit 180 s; jeho konec nebyl vydáván za průchod, browser byl ověřen znovu zvlášť.
- Nové focused testy navigace, eventové brány a zvukové brány: 7 prošlo.
- `node --test tests/wns-v2-assets.test.mjs` z `mobilni-aplikace/`: prošlo;
  SHA-256 převzatých art/audio/font/icon souborů odpovídá zdrojovému manifestu,
  demo Matylda ani sampler nejsou v nové aplikační sadě.
- `npm run test:opening:browser --prefix mobilni-aplikace`: 58 snímků, bez JS
  chyb; vstup → formulář → souhlas → čekání → odhalení → vstup, šířky
  320/375/390/430, dva starší theme identifikátory, chyby/offline/odebraný přístup.
  Kontrolováno i skutečné computed Manrope, nikoli jen deklarace v CSS.
- `npm run test:game:browser --prefix mobilni-aplikace`: 92 snímků; lokální
  výběr ještě neposílá příkaz, potvrzení posílá správnou volbu, sedm tréninkových
  výměn, taktická zásoba, sestava, čtyři týmové stavy, boss/zajetí a YOLO odhalení,
  čtyři šířky a 200% text. YOLO odpověď je fixture, ne živý provider.
- `npm run test:browser --prefix mobilni-aplikace`: prošlo 14 route pohledů,
  customer/staff oddělení, zachování konceptů správy bez zápisů, passkey/účty,
  data a odkazy, klávesnice/fokus, 1440/390/320 a 200% text. Opraven skutečný
  horizontální přetok dlouhého nadpisu při 200% písmu; ne pouze skryt overflow.
- Backend `--mobile-game`, `--mobile-game-boss`, `--mobile-game-teams`,
  `--mobile-game-progress`: všechny skupiny doběhly s exit 0.
- `--sealed-case`: prošlo po reprodukci a opravách nálezů prvního review.
  Regrese pokrývají pomoc bez útočného hodu, prioritu současných následků,
  neprodlužování bezvědomí, skrytí ostatních adresátů, autorovo publikování/doručení,
  neplatné enumy, stavové brány snu/ozvěny před pohybem, spotřebu/expiraci přípravy
  a odkazy na vlastní prožitky. Nepodporované podmínky akcí fáze 2 se odmítají,
  nikoli tiše ignorují. Statické re-review opraveného foundation subsetu
  nepotvrdilo další konkrétní defekt. Následně doplněny další kombinace pomoci,
  úplná matice snových/ozvěnových akcí a ověření přípravných modifikátorů.
  Model review byl doložen jako gpt-6.1-sol; efektivní reasoning effort nebyl
  zpřístupněn, proto není doloženou součástí přejímky.
- `--sealed-case-persistence`: prošly kontroly úložiště včetně restartu,
  idempotence, špatné skupiny/operace/revize, neplatného startu a pozdního příkazu.
  Test byl přejmenován z chybného „HTTP“ názvu; žádné HTTP cesty tím nejsou ověřeny.
  Nový souběžný test odhalil rozdělené čtení revize/historie; po zavedení jedné
  read transakce prochází i souběžné čtení a zápis.
- `--sealed-case-rules`: prošly kontroly 45min tutorialu, tří full časových pásem,
  počtu Spiklenců, přesných nominací/abstence, hraniční shody a serverového pause/resume.
- `--sealed-case-http`: nový skutečný loopback HTTPS server s existující Identity
  cookie, sessions, CSRF a launch middleware; osm syntetických účtů a cizí skupina.
  Prošlo založení/retry, odmítnutí chybného setupu a změněného retry, server-bound
  player/group, souhlas/připravenost, souběžné CAS zápisy, odvolání připravenosti,
  stale revize, přečtení z nového store, odebraný přístup, zákaz surového journalu
  a vypnutá funkce bez vzniku DB. Start/akce mají ověřené odmítnutí, nikoli úspěch.
  Registrace je v produkčním `Program.cs`, ale vyžaduje `SealedCase:Enabled=true`;
  žádná provozní konfigurace ani nasazení nebyly měněny.
  První HTTP review našlo společný IP limit a závod revokace před čekajícím zápisem.
  Obě chyby byly reprodukovány: polling vracel 429, odvolaný rozpracovaný zápis
  prošel. Po opravě prochází izolované per-account read/write limity i skutečný
  závod revokace při drženém SQLite writer locku, zvlášť pro založení a lobby zápis.
  Identity admission rezervace se bere po získání lobby locku a drží do commitu;
  kontroluje session/stamp, roli, skupinu a její membership revizi i u retry.
  Samostatný účet může nadále vyčerpat vlastní čtení bez blokování jiného účtu,
  vlastních zápisů nebo společných account endpointů. Statické re-review oprav
  nepotvrdilo další doložený defekt tohoto setup/lobby subsetu; doplňující
  varianty závodů session/stamp/role/retry a přesné hranice rate oken nejsou přejaté.
- `--mobile-game-http`: po novém HTTP slice znovu prošla i existující herní HTTP sada.
- `BetaAdmissionPanel.spec.ts` a `LaunchManagementPanel.spec.ts`: prošlo 17 testů.
  Ověření zdrojů opravilo dílčí audit: `LaunchManagementPanel` skutečně obsahuje
  `BetaAdmissionPanel` a ten už používá skupinové save/confirm/start operace s revizemi.
- `npm run build --prefix mobilni-aplikace` a `git diff --check` prošly.
  Build stále hlásí velký chunk a neúčinný dynamic import `servoBench`, ne nový
  důkaz nativní funkčnosti.

Lokální evidence této relace je v
`/workspace/.tmp/dildovander/wns-design.23duwF/evidence/`.
Browser instalace používá zapisovatelný `PLAYWRIGHT_BROWSERS_PATH` v téže
soukromé pracovní složce; systémový cache mount nebyl měněn.

## Zatím neověřeno

### Čeština — ověřený rozdíl oproti dílčímu auditu

- `interfaceLanguage.ts` již normalizuje uložené `en` na `cs`; samotná přítomnost
  legacy typu `en` není důkaz, že lze rozhraní přepnout do angličtiny.
- Ze skutečného nastavení rozhovoru byl odstraněn jazykový select; explicitní
  uložení nyní posílá `cs-CZ` i po načtení staré preference `en-US`.
  Prošly focused komponentové testy, typecheck, build a celý `test:browser`.
  Browser ověřil i legacy hodnotu v localStorage a API odpovědi, bez automatického
  zápisu; snímky jsou v `evidence/application-czech/` pod výše uvedeným kořenem.
- Serverové trusted instrukce i nový Live snapshot nyní vynucují češtinu, včetně
  starého English snapshotu bez preference. Realtime STT používá `cs`; nový
  běhový kontext používá `cs-CZ` i pro samostatný WAV přepis z legacy požadavku.
  Uložené preference, přijaté payloady a historie se nepřepisují ani nepřekládají;
  staré `en-US` lze nadále načíst jako historická metadata, ne jako volbu nové řeči.
- Prošly `--customer-preferences`, `--conversation-engines`, `--voice-providers`
  a `--persona-lab`. HTTP fixture bylo nutné opravit na výslovný souhlas se
  založením profilu; používá lokální testovací enrichment provider, ne OpenAI.
  Před doplněním hlavních runtime hranic selhaly reprodukce legacy WAV locale
  a chybného early-return u starého Live snapshotu; po opravách procházejí.
  Statické nezávislé review serverového Czech-only diffu nenašlo blokující nález.
  Doporučené silnější testy byly doplněny: původní skutečná anglická instrukce
  i hash starého snapshotu zůstávají beze změny, nový hash zahrnuje českou politiku;
  původní request locale/bytes a journal hash zůstávají stejné, přesný retry
  vrací tutéž konverzaci a změněný český payload se stejným klíčem je odmítnut.
  `--voice-providers`, `--conversation-engines`, `--customer-preferences` a
  `--sealed-case-http` po doplnění znovu prošly. Živá AI/audio stále neověřeny.
- Nový skutečný viewport 390/320 navíc ověřuje dosažitelnost tlačítka uložení
  nad pevnou navigací přes `elementFromPoint`; full-page screenshot s lištou
  uprostřed dlouhé stránky není sám o sobě důkaz trvale zakrytého ovládání.

Živá provider AI, skutečné víceuživatelské účty/partie, passkey na fyzickém
telefonu, iOS/Android signing a instalace, nativní export/haptika, plná hra Pod
pečetí a lidský pilot. Nic nebylo nasazeno. Stav těchto položek není „hotovo“
jen proto, že sestavení nebo syntetický browser test projde.
