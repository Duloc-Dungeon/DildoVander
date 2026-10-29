# WNS v2 — implementace a skutečně provedené kontroly

## Příprava postavy a návrat do čekárny — 29. října 2026

- Před otevřením večera `/launch` ukazuje kroky Postava → Portrét → Hůlka →
  Čekárna podle načteného stavu. Během ukládání a přípravy portrétu zůstává
  viditelný vtip: přednost má `artwork.loadingMessage`, bez něj uvítací věta
  nebo stabilní český náhradní text. Polling vtip náhodně nestřídá.
- Po odhalení portrétu se zpřístupní vstup do dílny. Selhání portrétu ukáže
  náhradní podobu, možnost opakování a cestu do dílny bez obrázku. Samotný čas
  neoznačí přípravu za hotovou. U postavy bez objednaného portrétu text
  neoznamuje běžící výrobu; zůstává výslovná akce pro přípravu.
- Otevření dílny skryje postavu, odpočet i přehled kroků, aby hlavní akce
  výsledku zůstala dostupná i na malém telefonu. Po uložení a návratu se
  zobrazí čekárna s postavou, jejím vtipem, názvem hůlky a akcí
  **Prohlédnout hůlku**. Obnovení stránky načte hotovou hůlku ze serveru;
  prohlížení nezakládá novou výrobu. Oprávnění a připravenost pro samotnou
  výrobu nadále ověřuje [dílna](magicky-kufr-workshop.md#api-a-výroba).
- Lokálně prošlo 47 testů v `LaunchView.spec.ts`, `PrelaunchWand.spec.ts`
  a `PortraitCurtain.spec.ts`, produkční build a nezávislé review.
  `test:prelaunch-wand:browser` ověřil čekání, chybějící portrét, dílnu,
  uložený výsledek, návrat a obnovení při šířkách 320/390/1280 px,
  omezeném pohybu a 200% textu; uložil 20 syntetických snímků.
  Prošel také `test:launch:browser` včetně vytvoření postavy, soukromého
  portrétu, označení postavy a přepnutí účtu. Mechanický sken změněných
  obrazovek a `git diff --check` byly čisté. Jde o lokální webové fixtures,
  nikoli živou AI, přejímku na fyzickém telefonu nebo nasazení.

## Přihlášený host — 29. října 2026

- Navigace vede přímo do digitální Dílny, Her, Památek a účtu. Postava zůstává
  dostupná z Dílny a je prvním krokem po registraci. Žebříček je pojmenovaný
  přímo v herních odkazech i prázdné kolekci.
- `/me/conversation` je kompatibilní přesměrování do `/play/wand` se zachovanou
  kontrolou účtu na cílové trase. Zákaznický pohled již neimportuje Bluetooth,
  nenačítá fyzické relace ani nenabízí jejich spouštění. Servisní části a
  protokoly zůstávají oddělené; nejde o změnu firmware ani backendových kontraktů.
- Památky upřednostňují kolekci. Aktualizace, tituly, případy a souboje se
  načítají až po otevření příslušné sekce. Prázdná nevyfiltrovaná kolekce
  neskrývá užitečný další krok za hledání a filtry; vyfiltrovaný prázdný stav
  a chyba zachovávají ovládání hledání.
- Zákaznické nadpisy a navigace jsou upravené pro zvětšený text. Texty účtu,
  podpory a památek používají tykání. Ověření a jeho limity se uvádějí v dodání
  této změny; historické výsledky níže nejsou jejím automatickým potvrzením.

## Osobní hůlky v herním souboji — 29. října 2026

- `GameClash.vue` používá celé průhledné PNG 2:3. Hrot určuje z alfa kanálu;
  zdrojovou kresbu neořezává ani nepřebarvuje. Úder, lest a štít mají odlišnou
  stopu, krátký světelný náraz a částice. Efekt trvá nejvýše dvě sekundy,
  neblokuje další tah a neopakuje se při pollingu ani otevření historie.
  Omezený pohyb, skrytí a opuštění scény animaci vypnou.
- Vlastní obrázek přichází ze stávajícího autorizovaného odkazu aktuální
  postavy. Odpověď `GET /app/v1/game/matches/{id}` může nově obsahovat
  `opponent.wandArtworkUrl`, `wandName` a `wandSynthetic`. Pole jsou volitelná;
  bez přístupného obrázku klient zobrazí neutrální obrys.
- Obrazová cesta je `GET /app/v1/game/matches/{id}/opponent/wand-artwork?revision={sha256}`.
  Ověřuje skupinu a účastníka zápasu, aktuální vybranou hůlku soupeře,
  platné vlastnictví památky, schválení aktuální publikované edice a přesný
  kanonický PNG se stejnou revizí. Přijetí souboje nenahrazuje souhlas
  se zveřejněním. Neplatná nebo odvolaná reference vrací 404.
  Odpověď používá `no-store` a `nosniff`; identifikátory vlastníkovy památky
  ani privátní cesty úložiště se soupeři neposílají.
- Jde o aktuální stav postavy, nikoli obrázek zmrazený při zahájení zápasu.
  Změna či odvolání zveřejnění se projeví při dalším ověření stavu; obrazový
  endpoint kontroluje oprávnění znovu při každém čtení. Metadata při pollingu
  nenačítají celé PNG. Web/iOS/Android povolují jen přesnou obrazovou cestu
  a stávající limit obrazových přenosů; klient nepřijímá libovolné URL.
- `tests/fixtures/generated-wands/` obsahuje dvě syntetické PNG vytvořené
  vestavěným ImageGen podle `KeepsakeProvider.BuildWandImagePrompt`.
  `generation.json` uchovává prompty, fiktivní návrhy a kontrolní součty.
  Fixture server je podává odděleně od aplikačního balíku; nejsou produkčním
  výsledkem hráče. Náhledy: `/__game/wand-moon` a `/__game/wand-thorn`.

Pro lokální ověření slouží `node tests/game-clash.browser.mjs` z mobilní
aplikace a backendová skupina `--game-wand-artwork`. Prošly také stávající
herní browserové scénáře v pěti šířkách včetně 200% textu a omezeného pohybu.
Skupina `--mobile-game-http` ověřuje i schválený PNG přes skutečnou HTTP cestu,
jeho přesné bajty a hlavičky a zneplatnění původního odkazu po odvolání publikace.
Tyto kontroly nenahrazují nativní iOS sestavení a přejímku na telefonu.

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

## Oprava vstupu a oddělení administrace — 28. října 2026

- `/` a historická `/demo` vedou přes přihlašovací scénu. Starý domov a
  syntetická ukázka byly odstraněny; archiv vyžaduje účet. Sdílené odkazy
  na památky a souboje si zachovávají původní ověření oprávnění.
- Uvítání, přihlášení a registrace sdílejí šířku, typografii a ovládání bez
  vnořených panelů. Ilustrace hůlky i klíče se celé vejdou do barevné plochy.
- Vlastník po přihlášení otevírá zákaznickou postavu. Administraci otevře
  z účtu; serverové role a oprávnění se změnou zobrazení nemění. Výběr hráčské
  skupiny zůstává ve hře a účtu, bez trvalého přepínače režimu na všech stránkách.
- Předvolby a výběr vzhledu či jazyka zmizely ze společného rozhraní.
  Zvuk zůstává dobrovolný; ovládání ukazuje ikonu, textový stav a `aria-pressed`.
- Lokální Windows ověření: build, 157 Node testů, 1 233 Vitest testů,
  `test:browser` a `test:owner:browser`. Syntetické browser průchody pokrývají
  320/390/1440 px, 200% text, vstup, souhlas, návratové adresy, zákaznickou
  navigaci, administraci, skupiny a zvuk. Není to přejímka na fyzickém iPhonu
  ani nasazení.

### Doplnění podle kritiky rozhraní — 28. října 2026

- Účet vlastníka nabízí přímo `Večer a pozvánky` a zvlášť `Účty a oprávnění`.
- Ruční první postava začíná jménem a předlohou; další údaje jsou pod
  `Doplnit postavu`. Sbalení zachovává návrh, při úpravě existující postavy
  jsou podrobnosti otevřené.
- Správa večera shrnuje skutečně načtenou skupinu, čekající hosty, přijímání
  hostů a otevření. Po neúspěšném obnovení uvádí neověřený stav. Odpočet
  a otevření jsou přímo dostupné, pokročilá nastavení jsou rozbalovací.
- Oddíl zpráv má vlastní dostupné uložení s výslovným upozorněním, že uloží
  i rozepsaný globální plán; zachovává společný revizní kontrakt. Chyba otevře
  příslušný oddíl a opětovné ověření samo neopakuje změnu.
- Prošel build, 157 Node testů, 1 239 Vitest testů a průchody
  `test:browser`, `test:owner:browser`, `test:launch:browser` v lokálním Brave
  přes stávající Playwright scénáře. Ověřeny byly syntetické stavy na
  320/390/1280/1440 px, u hlavního průchodu také 200% text; nejde o nativní
  přejímku telefonu. Mechanický designový sken změněných obrazovek byl čistý.

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

### První průchod hosta — 29. října 2026

`test:guest-journey:browser` spojuje sdílené UI se skutečným izolovaným HTTPS
backendem a virtuálním WebAuthn autentikátorem. Fixture vytvoří dočasného
vlastníka, zákaznickou pozvánku a skupinu s budoucím otevřením. Enrichment,
portrét i obrazový fallback mají lokální testovací implementace; test nevyžaduje
externí poskytovatele ani placenou generaci. Launcher po skončení ověří odstranění
dočasné databáze, klíčů a soukromé pozvánky.

Ověřeno: explicitní souhlas, zachování formuláře po simulovaném zrušení
přístupového klíče, skutečná registrace hosta a uložení postavy, ztracená odpověď
po dokončeném zápisu, obnovení stránky bez dalšího vytvoření, budoucí odpočet,
dekódování lokálního testovacího portrétu, opětovné přihlášení stejným klíčem
a obecné odmítnutí použité či neznámé pozvánky. Mobilní čekárna nově skrývá
nepovinné rozlišovací označení do rozbalovací sekce a po odhalení postavy nabízí
zkratku k hůlce; při chybě portrétu zůstává tento další krok dostupný.

Spuštění z kořene app repozitáře v PowerShellu na Windows:

```powershell
dotnet build backend-tests/BackendTests.csproj -c Release -r win-x64 --self-contained false -p:RestoreLockedMode=true
npm run build --prefix mobilni-aplikace
$env:KUFR_TESTS_DLL = 'backend-tests/bin/Release/net10.0/win-x64/BackendTests.dll'
$env:APP_SCREENSHOTS = Join-Path (Get-Location).Path '.codex/artifacts/guest-journey'
npm run test:guest-journey:browser --prefix mobilni-aplikace
```

Jde o lokální přejímku s virtuálním klíčem, nikoli důkaz chování na fyzickém
telefonu, živé AI nebo skutečné tvorby hůlky. Starší `test:accounts:fixture`
v tomto checkoutu před změnou stále očekával po registraci `/settings`, zatímco
aktuální aplikace přechází na `/launch`; nový zákaznický průchod tento starší
administrátorský scénář nenahrazuje.

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

## Oprava CI po úklidu zdrojů

- PR #321 odhalil, že `cleanup` odstranil spouštěné CI skripty, zatímco jejich
  workflow zůstala zachována. Ze základu `8c202e50ab4a86bf702203a49b79d4943c0e3fc1`
  byly obnoveny přímo potřebné CI, balicí, tokenové a syntetické viewer vstupy.
  Nebyly vypnuty kontroly, změněna pravidla propagace artefaktů ani obnoveny
  CAD exporty, modely či celý konstrukční strom.
- Obnovený `mechanism.py` slouží existujícímu twin testu k načtení dvou funkcí
  přes AST; nejde o spuštění CadQuery ani mechanickou přejímku. Obnovené DV02
  dokumenty a metadata jsou vstupy zachované kontroly inventáře, nikoli nové
  řízení práce: pravidla nativního Hermes a tento `AGENTS.md` mají přednost
  před historickými odkazy na projektové trackery.
- Syntetické cache testy nyní předávají identitu svého injektovaného mesheru,
  místo aby vyžadovaly nepoužívaný CAD renderer `preview.py`. Regrese navíc
  ověřuje, že změna mesheru nevrátí mesh z předchozí cache.
- `scripts/ci/test_workflow_inputs.py` hlídá existenci přímo odkazovaných CI
  skriptů a bootstrap dat; test s mazáním jednotlivých fixture vstupů ověřuje,
  že absence není tiše ignorována. Nenahrazuje následné buildy a runtime testy.
- Lokálně prošly testy provenience, balení webu, designového systému, bootstrapu,
  všech vyžadovaných syntetických viewer skupin a skutečné Gitleaks regrese
  verze 8.30.1. Výsledky nejsou tvrzením o zeleném vzdáleném CI ani nasazení;
  produkční preflight dosud blokuje nativní schválení.
