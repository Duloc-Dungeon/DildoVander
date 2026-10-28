# Samostatná dílna magického kufru

Mobilní cesta začíná na `/play/wand`; oprávněný upgrade používá
`/play/wand?upgrade=1`. Vstupy jsou v herní obrazovce a u návštěvy dílny.
Mluvčím je očarovaný kufr. ESP32, párování a mechanické příkazy nejsou potřeba.

## Průchod

Otevření stránky pouze načte přihlášenou postavu a existující postup. Tlačítko
**Otevřít dílnu** založí nebo obnoví jedinou operaci pro vlastníka, profil,
revizi a úroveň hůlky. Mikrofon je vypnutý, psaní je dostupné od začátku.

Server připravuje a ukládá každý povolený tah před jeho zobrazením a přednesem.
Stav `authoring` ještě neobsahuje otázku, kterou by smělo rozhraní přehrát.
Otázky vycházejí z profilu a předchozích odpovědí. Každá má stabilní ID a revizi.
První a druhá uzavřená odpověď připraví další otázku; třetí odpověď nebo výslovné
přeskočení třetího kroku zahájí výrobu jediné hůlky. „Nevím“ je platná odpověď.

Hlasový režim přehrává schválený uložený text a nabízí nahrání odpovědi.
Přepis je upravitelný návrh; až odeslání uzavře otázku. Ticho, chybějící
oprávnění, chyba přepisu a přepnutí na psaní nic nezapočítávají. Nejde o volný
GPT Live socket s následnou kontrolou již přehrané řeči. Po odchodu se mikrofon
i přehrávání uvolní. Návrat načte stejnou operaci bez automatického přednesu.

Před výrobou lze v přepisu opravit přijatou odpověď. Oprava mění její revizi,
nikoli počet kroků. Při nejistém výsledku požadavku klient nejdřív načte stav
původní operace. Nový placený pokus smí nabídnout pouze server přes `canRetry`.

Hotový výsledek má vlastní `WandId`, název, materiál, jádro, temperament, příběh,
vzhled, afinitu, slabost a tři kouzla. Herní čísla dál vlastní server. Mobilní
veřejný výsledek nemá fyzický slot; původní šestikusová větev zůstává samostatná.

## Hranice API

Základ je `/app/v1/customer/wand/workshop`. Vyžaduje zákaznické přihlášení,
platný souhlas a aktuální herní oprávnění. JSON používá camelCase.

| Metoda a cesta | Význam |
| --- | --- |
| `GET /workshop[?upgrade=1]` | `{eligible, reason, profile, tier, session}`; žádná placená operace |
| `POST /workshop/sessions` | `{operationId, upgrade}`; explicitní začátek, odpověď `{session}` |
| `GET /workshop/sessions/current[?upgrade=1]` | Obnovení pro aktuální profil a úroveň |
| `GET /workshop/sessions/{id}` | Autoritativní stav stejné operace |
| `POST /workshop/sessions/{id}/answers` | `{questionId, questionRevision, answer, sourceMode}` |
| `POST /workshop/sessions/{id}/skip` | `{questionId, questionRevision}`; pouze aktivní krok |
| `PUT /workshop/sessions/{id}/answers/{answerId}` | `{answerRevision, answer, sourceMode}`; oprava |
| `POST /workshop/sessions/{id}/audio` | `{questionId, questionRevision}` nebo `{wandId}`; WAV schváleného tahu |
| `POST /workshop/sessions/{id}/transcribe` | Syrové zvukové tělo do 8 MiB; `{text}`, bez uzavření otázky |
| `POST /workshop/sessions/{id}/retry` | Serverem povolená obnova téže operace |
| `POST /workshop/sessions/{id}/artwork/retry` | Obnova asociace nebo samotné ilustrace; UUID v `Idempotency-Key` |

Stavy průběhu jsou `authoring`, `asking`, `generating`, `complete`, `failed`.
Otázka má `questionId`, `questionRevision`, `number`, `total`, `question`.
Uzavřené kroky mají navíc `answerId`, `answerRevision`, `answer`, `skipped`
a `sourceMode`. Nový socket ani opakované doručení odpovědi nevytváří další krok.
Přepis vyžaduje `X-Wand-Question-Id` a `X-Wand-Question-Revision`, aby opožděná
nahrávka nebyla přiřazená k otázce změněné jiným klientem.
Původní volný mobilní `/sessions`, `/ticket` a `/live` vracejí 410.

## Kontext, dostupnost a ilustrace

Kontext je svázaný s vlastníkem a revizí profilu. Generátor dostává celé
obohacení včetně faktů, zdrojů a nejasností, nikoli pouze souhrn nebo `Take(6)`.
Odpovědi zůstávají oddělenou evidencí a nemění domněnku či vtip v nový fakt.
Interní kontext nezahrnuje skrytou pravdu domény Pod pečetí.
Současný zákaznický model neposkytuje této relaci ověřený profilový souhlas
pro dlouhodobou paměť minulých rozhovorů; dílna proto z těchto záznamů neodvozuje
další znalosti. Opravy používají aktuální revizi profilu a aktuální revizi
odpovědi ve výrobě.

Při zajetí dílny bossem server zakáže novou výrobu i pokračování aktivního
rozhovoru. Uloženou hůlku lze dál zobrazit. Nový host bez hůlky má přístup
k exhibiční záchraně proti bossovi; běžné duely stále vyžadují vlastní hůlku.

Po potvrzeném uložení se hůlka propojí s vlastním záznamem v kanonickém archivu
a jeho úlohou `wand_artwork`. Odkaz obsahuje vlastníka, `WandId`, locator a ID
assetu. Obraz se stahuje autorizovaně z
`/app/v1/archive/owned/{locator}/assets/{assetId}` a předává scéně jako lokální
blob URL. Ilustrace má vlastní `queued/running/ready/failed` stav. Její chyba
nezruší uloženou hůlku; opakování obrázku nevyrábí novou hůlku.

## Vývojové ověření

Použij podporovaný Node z `mobilni-aplikace/.nvmrc` a SDK z `global.json`.
Z kořene produktového repozitáře:

```text
npm ci --prefix contracts
npm ci --prefix mobilni-aplikace
npm run build --prefix mobilni-aplikace
npm run test:wand:browser --prefix mobilni-aplikace
dotnet build backend-tests/BackendTests.csproj
dotnet run --project backend-tests/BackendTests.csproj --no-build -- --mobile-wands
dotnet run --project backend-tests/BackendTests.csproj --no-build -- --generated-wands
dotnet run --project backend-tests/BackendTests.csproj --no-build -- --mobile-game-http
```

Pro místní vizuální ukázku sestavené aplikace:

```text
node mobilni-aplikace/tests/fixtures/wand-workshop.mjs --serve
```

Otevři `http://127.0.0.1:4805/__workshop/ready`. Jde o výslovně syntetický
testovací server, který nepoužívá poskytovatele ani skutečný účet. Varianta
`/__workshop/captured` ukazuje nedostupnost. Tato ukázka neprokazuje živé API.
Skutečný průchod otevři v přihlášeném vývojovém prostředí přes herní vstup nebo
výše uvedené routy; backendový klíč se do klienta nepřenáší.

Browser harness ověřuje 320/390/1280 px, text 200 %, klávesnici, sníženou výšku
420 px, omezený pohyb, obnovení a nezávislé opakování ilustrace. Snížená výška
simuluje prostor pro soft klávesnici; skutečná klávesnice, notche, safe areas
a nativní přehrávání vyžadují ověření podepsané aplikace na zařízení.

### Provedené ověření 28. 10. 2026

- Node 24.21.0: 155 testů Node a 1 073 testů Vitest prošlo; frontendový build
  prošel včetně kontroly generovaných kontraktů a skutečných WebP v balíku.
- Backendový build prošel bez varování a chyb. Prošly skupiny `--mobile-wands`,
  `--generated-wands`, `--mobile-game-http` a `--mobile-game-boss` včetně
  zachování staré hůlky, smazání dat, revokace, souběhu a obnovy po timeoutu.
- Syntetický browser test prošel na 320, 390 a 1280 px, s textem 200 %,
  omezeným pohybem a sníženou výškou 420 px. Java pravidla nativního transportu
  prošla samostatným testem; úplný Android Gradle test blokovalo prostředí
  a iOS testy nebylo možné spustit bez Swift compileru.
- Jediný živý průchod s fiktivní postavou položil tři schválené otázky
  a po třech psaných odpovědích vytvořil a uložil jedinou hůlku **Modrosteh**.
  Následná kontrola přednesu stejných tří tahů přes skutečné TTS a STT
  dosáhla normalizované textové shody 100 %, 98,4 % a 98,9 %. V přepisech
  zůstala vždy jedna otázka; příprava otázek ani výroba se neopakovaly.
- Záznam stejného uloženého `WandId` pokračoval přes `KeepsakeStore` a jedinou
  skutečnou obrazovou úlohu do vlastního kanonického PNG. Ověřilo se uložení,
  čtení vlastníkem a odmítnutí cizího účtu. Model `gpt-image-1.5` byl ověřený
  v dostupnosti účtu a v [oficiální dokumentaci OpenAI](https://developers.openai.com/api/docs/models/gpt-image-1.5).
  Jeho nastavení bylo pouze v izolovaném testu; produkční konfigurace se neměnila.

Živé diagnostiky jsou výslovné přepínače v `backend-tests`, nejsou součástí
běžného offline CI. Jejich hlasové, obrazové a databázové doklady jsou lokálně
v ignorovaném `ai-eval/results/`. Nový skutečný běh potřebuje nakonfigurovaný
backendový klíč a reálnou obrazovou pipeline (`Keepsake:ProviderMode=openai`,
`Keepsake:ImageModel` a ostatní nastavení stávajícího archivu). Chybějící či
falešný poskytovatel se hostovi nevydává za ilustraci jeho hůlky.

## Výtvarné zdroje

Použité WebP jsou nezměněné exporty dodaného balíčku
`DildoVander-magicky-kufr-assets-v1.zip`, uložené v
`mobilni-aplikace/src/assets/wns-v2/art/trunk-workshop/`. Manifest uchovává
rozměry a kontrolní součty původního balíčku. Otevřený a zavřený kufr jsou
samostatné statické obrazy. Jemná reakce celého objektu vychází z měření
přehrávaného zvuku; při `prefers-reduced-motion` se vypíná.

Podnos není pro neznámou orientaci kanonického artworku použitý. Výsledek se
zobrazuje samostatně bez ořezu. Demonstrační hůlka `sever-wand-demo.png` není
součástí klientského buildu ani výrobního fallbacku.
