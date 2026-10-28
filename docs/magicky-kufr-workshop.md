# Samostatná dílna magického kufru s GPT Live

Cesta `/play/wand`, oprávněný upgrade `/play/wand?upgrade=1`. Mluvčím je
magický kufr. Telefon nepotřebuje ESP32, párování ani mechanické příkazy.
Implementace navazuje na commit `8e181d99`; nový průchod nahrazuje pevné tři
kroky přirozeným rozhovorem. Přibližně tři otázky jsou pouze dramaturgické
vodítko, žádná přesná garance ani podmínka výroby.

## Připravená postava a vědomý začátek

Otevření stránky pouze čte stav. Backend před začátkem, obnovením, ticketem,
přijetím sdělení a výrobou kontroluje aktuální profil a revizi. Připravenost
znamená `status=ready`, dokončené obohacení se shodným `inputRevision` a žádné
nevyřešené upřesnění. `CapturePreparedAsync` čte tyto hodnoty v jedné transakci.
Starší dokončená revize neodemkne novější rozpracovanou postavu.

Při přípravě, chybě nebo potřebě upřesnění nejsou dostupné hlasové ani textové
zahájení. Obrazovka nabízí návrat k postavě. Po dokončení se objeví
**Promluvit s kufrem** a **Raději psát**; nic se automaticky nespouští.
Hlasová větev ověří zákaznický ticket před vyžádáním mikrofonu a oprávnění
ověří znovu při WebSocket upgrade. Ticket je jednorázový, svázaný s vlastníkem,
operací, přihlášením a přesným originem; platí 30 sekund.

## Hlas a společný rozhovor

`MobileWorkshopLiveRuntime` používá existující `GptLiveClient`,
`GptLiveProtocol.Model` (`gpt-live-1`) a skutečný client-delegation protokol.
Frontend sdílí nízkoúrovňový PCM resampler, AudioWorklet a omezené přehrávání
z `persona-lab/voice.ts`, ale používá výhradně zákaznické endpointy dílny.
Nevolá staff PersonaLab API. Starý `MobileWandRuntime` zajišťuje jen ticket a
základní admission; jeho předgenerování kandidátů a starý rozhovor byly odstraněny.

Po vědomém spuštění poslouchá mikrofon průběžně. Běžné hlasové odpovědi se
ručně nenahrávají ani neodesílají. Přehrávání lze přerušit řečí nebo **Stop řeč**;
ztlumení mikrofonu a ukončení rozhovoru jsou samostatné akce. Stop je dostupný
i při výsledném verdiktu. Odchod, skrytí aplikace, změna účtu/souhlasu nebo režimu
uvolní audio a mikrofon. Pozdní svolení k mikrofonu neobnoví opuštěnou relaci.
Běžné obnovení údajů stejného účtu se stejným souhlasem hovor ani rozepsaný
text neresetuje. Při skutečné změně vlastníka se odstraní i místní titulky a opravy.
Při změně profilu nebo jeho revize na otevřené stránce se starý místní kontext
odpojí a přijme aktuální stav serveru. Další vědomé spuštění použije operaci
aktuální revize; rozpracovaná starší operace zůstává uložená u původního kontextu.

Střídání řeči řídí GPT Live. Zákaznická větev neposílá Stop podle samotné
hlasitosti mikrofonu: ozvěna reproduktoru ani nárazový hluk nesmějí ukončit
rozeznělou otázku. Ruční **Stop řeč** zůstává samostatným příkazem.
Transport skládá rozdělené WebSocket zprávy s omezenou velikostí a počtem částí.
Oprávnění průběžně ověřuje server; čtení celého profilu není součástí odeslání
každého dvacetimilisekundového audio paketu. Provozní diagnostika zapisuje pouze
schválenou kategorii chyby, nikdy text postavy, přepis ani odpověď poskytovatele.

Čtení stavu používá oddělený `account-poll` limit; příkazy ponechávají původní
limit `account`. Během hlasu se HTTP stav obnovuje po deseti sekundách.
Souběžné obnovy se slučují, jeden požadavek má patnáctisekundový limit a po
chybě následuje postupné zpomalení nejvýše na třicet sekund. Dočasná chyba čtení
nezastaví fungující hlas ani nepřepne režim a po zotavení hláška zmizí.
Odpojení během již odeslané delegace může do jejího dokončení (nejvýše 45 sekund)
držet relaci obsazenou; zákazník dostane vysvětlení a obnovuje stejnou operaci.

Rychlé delegace mají omezenou sériovou frontu; další požadavek se nezahazuje,
pokud právě dobíhá předchozí. Nová sdělení mohou rozšířit podklady pokračující
repliky, ale dokončení vyžaduje přesnou shodu uživatelských podkladů. Zastaralé
dokončení ani závod s opravou nezahájí výrobu a neukončí hlasové spojení.
Live dostává výslovný stav této výroby: rozhovor, přijatá výroba, nebo potvrzený
uložený výsledek. Běžná odpověď backendu není potvrzení hotové hůlky.

Průběžný přepis není potvrzená odpověď. Server slučuje a ukládá přijaté textové
úseky se stabilním ID; dlouhou souvislou řeč dělí beze ztráty do bezpečných bloků.
Sdělení mají `messageId`, `revision`, `role`, `text`, `sourceMode`, `createdAt`.
Oprava uživatelského sdělení vyžaduje očekávanou revizi. Po zahájení výroby se
podklady uzamknou. Přepnutí hlas → text → hlas zachová operaci i přijatou historii.

Delegace Live neobsahuje zadání; backend používá uložené aktuální přepisy,
nikoli domnělé argumenty v události. Rozhodnutí modelu navrhne pokračování nebo
dokončení. Backend kontroluje oprávnění a atomicky porovná digest uživatelské
evidence. Pozdní oprava zneplatní starý návrh, běžné hlasové přitakání kufru jej
nezneplatní. Potvrzení append události znamená doručení kontextu, nikoli skutečné
přehrání audia. Reakce kufru vychází z měření skutečného audio výstupu.

Oficiální protokol: [Live sessions](https://developers.openai.com/api/docs/guides/live-conversations),
[client delegation](https://developers.openai.com/api/docs/guides/live-delegation),
[playback controls](https://developers.openai.com/api/docs/guides/voice-server-controls?api=live).

## API a výroba

Základ `/app/v1/customer/wand/workshop`, zákaznické přihlášení, souhlas a aktuální
herní oprávnění. JSON má camelCase. Cesty v tabulce jsou relativní k základu.

| Metoda a cesta | Význam |
| --- | --- |
| `GET /[?upgrade=1]` | `{eligible,reason,profile,tier,session}` bez placeného volání |
| `POST /sessions` | `{operationId,upgrade}`, založení/obnova, odpověď `{session}` |
| `GET /sessions/current[?upgrade=1]`, `GET /sessions/{id}` | Obnova téže operace |
| `POST /sessions/{id}/ticket` | `{socketOrigin}`, jednorázové zákaznické Live připojení |
| `GET /sessions/{id}/live?ticket=…` | Autorizovaný WebSocket: PCM24kHz, přepis a oddělené backendové stavy |
| `POST /sessions/{id}/messages` | `{messageId,text}`, idempotentní textové sdělení a odpověď kufru |
| `PUT /sessions/{id}/messages/{messageId}` | `{expectedRevision,text}`, oprava před výrobou |
| `POST /sessions/{id}/finish` | Samostatné idempotentní dokončení rozhovoru |
| `POST /sessions/{id}/retry` | Jen serverem povolená obnova známého bezpečného selhání výroby |
| `POST /sessions/{id}/audio` | `{wandId}`, pomocné předčítání uloženého verdiktu |
| `POST /sessions/{id}/artwork/retry` | Jen chybějící/nezdařený obrázek, UUID `Idempotency-Key` |
| `POST /sessions/{id}/artwork/repair` | Explicitní oprava hotové ilustrace, očekávaná verze a zdroj, UUID `Idempotency-Key` |

Aktivní stavy jsou `asking`, `generating`, `complete`, `paused`, `failed`.
Proměnlivý počet sdělení neslouží jako výrobní čítač. `FinishWorkshopConversation`
vyžaduje platný připravený profil a použitelný rozhovor, atomicky zamkne podklady
a přejde do výroby nejvýše jednou. Textový model může navrhnout dokončení;
uživatel také může zvolit **To stačí, vyrob hůlku**. Žádná třetí položka sama
výrobu nespouští. Před opakováním klient nejdřív načte původní operaci.

Generuje se jediná mobilní hůlka, nikoli šest předem připravených kandidátů.
Úplný zmrazený profil a všechny aktuálně přijaté repliky vstupují do výroby;
herní čísla vlastní server. Výsledek obsahuje vlastní `WandId`, materiál, jádro,
temperament, příběh, vzhled, afinitu, slabost a kouzla. Fyzická šestikusová větev
zůstává oddělená. Odchod nebo odpojení hlasu již přijatou výrobu nezruší.

Staré otevřené kroky se migrují do sdělení včetně odpovědí a revizí. Hotové hůlky
a výrobní operace zůstávají. Rozpoznaná stará dočasná selhání při bossově zajetí
se převádějí na pauzu; trvalá ztráta oprávnění se tím neobchází.

## Kontext a dočasná nedostupnost

Snapshot obsahuje celý aktuální popis, obohacení, fakta, zdroje a nejasnosti.
Žádné `Take(6)` ani samotný Summary. Pokud se plný záznam nevejde do startu Live,
je to výslovně uvedené a úplný backendový kontext vstupuje do delegace. Backendový
Responses požadavek má explicitní limit 65 536 bajtů; překročení vrací chybu,
nepředstírá úplnou znalost. Dlouhé přepisy mají také explicitní bezpečnostní limit.
Interní zdroje, modelové domněnky a vtipy se nevydávají za další ověřená fakta.
Žádná skrytá WORLD_TRUTH ani soukromá data jiného hosta se neposílají.
Dlouhodobá osobní paměť se bez profilově ověřeného souhlasu nepřipojuje.

Bossovo zajetí pozastaví rozpracovanou operaci a hlas. Po osvobození se znovu
kontroluje vlastník, členství, přístup, souhlas a připravená revize, poté lze
pokračovat ve stejném rozhovoru. Již dispatchnutá rozhodnutí mají vlastní omezenou
životnost a při dočasném zajetí se zachovají; hotový placený výrobní výsledek se
nevolá znovu. Odebrání souhlasu, účtu či profilu relaci neoprávněně neobnoví.

Výstavní záchrana umožní hráči bez hůlky a s 0 XP skutečně vyhrát: server má pro
exhibici minimální sílu 1 a energii bosse 3. Pravidla běžných duelů a upgradů se
nemění. Nejde pouze o odkaz na souboj s nulovým poškozením.

## Transparentní artwork a opravy

`wand_artwork` používá SDK 2.14 `Background=Transparent`, PNG1024×1536 a prompt
`personal-wand-art-v2-transparent`. Prompt vyžaduje celou izolovanou siluetu,
volné okraje, bez podlahy, stěny, podstavce, vržených stínů a nakreslené šachovnice.
Materiál a magické efekty vycházejí z uložené konkrétní hůlky. Alfa se validuje
dekódováním skutečných PNG bajtů: prázdné a plně neprůhledné obrázky jsou odmítnuté.
Portréty toto pravidlo nedostávají. Alfa kanál se při uložení/doručení nemění.

Ilustrace má nezávislé `queued/running/ready/failed`. Chyba neodstraní hůlku.
Oprava hotové ilustrace přijímá `{expectedVersion,sourceAssetId,sourceSha256}`;
aktivní reference obsahuje verzi, SHA a `canRepair`. Nová obrazová verze se aktivuje
až po validaci a kontrole stále aktuálního zdroje/job ID. Původní asset i SHA zůstávají
jako provenance. Nemění se `WandId`, vlastnosti, historie ani výrobní operace.
Dříve publikované edice a jejich sdílené odkazy ponechávají původní neměnné bajty.
Oprava canonical obrázku zpětně nepřepisuje vydané edice.

Konfigurace vyžaduje skutečný klíč, `Keepsake:ProviderMode=openai`, povolenou
stávající keepsake pipeline a model s podporou transparentních PNG. Chybějící
konfigurace nesmí být vydávána za skutečně generovaný artwork.
[Oficiální Images API](https://developers.openai.com/api/reference/resources/images/methods/generate).

## Spuštění a ověření

V přihlášeném vývojovém prostředí dokonči přípravu postavy, otevři `/play/wand`
a vědomě zvol hlas nebo psaní. Nastavení produkce se tímto úkolem nemění.
Syntetický browser preview: `node mobilni-aplikace/tests/fixtures/wand-workshop.mjs --serve`,
`http://127.0.0.1:4805/__workshop/ready`; varianty `preparing` a `captured`.
Preview nevolá poskytovatele.

Relevantní příkazy z kořene produktu:

```text
dotnet build backend-tests/BackendTests.csproj --no-restore
dotnet run --project backend-tests/BackendTests.csproj --no-build --no-restore -- --mobile-wands
dotnet run --project backend-tests/BackendTests.csproj --no-build --no-restore -- --mobile-workshop-socket
dotnet run --project backend-tests/BackendTests.csproj --no-build --no-restore -- --mobile-workshop-live-http
dotnet run --project backend-tests/BackendTests.csproj --no-build --no-restore -- --mobile-game-http
dotnet run --project backend-tests/BackendTests.csproj --no-build --no-restore -- --mobile-game-boss
dotnet run --project backend-tests/BackendTests.csproj --no-build --no-restore -- --generated-wands
dotnet run --project backend-tests/BackendTests.csproj --no-build --no-restore -- --wand-artwork
npm test --prefix mobilni-aplikace
npm run build --prefix mobilni-aplikace
npm run test:wand:browser --prefix mobilni-aplikace
```

Skutečné placené diagnostiky jsou pouze explicitní přepínače `--mobile-workshop-live`,
`--mobile-workshop-live-playback` a
`--mobile-workshop-transparent-artwork <sourceDatabase> <imageModel>`.
Používají jen fiktivní data a kontrolují již existující operace před dispatch.
Doklady jsou v ignorovaném `ai-eval/results/`, tajné klíče se nevypisují.

`--mobile-workshop-runtime-live` je další explicitní placená diagnostika. Volá
skutečný `gpt-live-1` přes zákaznické HTTP zahájení, ticket a
`MobileWorkshopLiveRuntime`; používá dva mluvené fiktivní vstupy a čtení stavu.
Backendová rozhodnutí jsou v tomto testu záměrně offline a výroba se nespouští.
Nejde proto o nový důkaz živého generování hůlky. Cílem je 65 sekund hovoru,
nouzový limit po připojení je 90 sekund. Ukládá WAV, přepis a technický doklad;
značka pokusu brání slepému placenému opakování. Věcnou správnost replik ověř
z celého přepisu a zvuku, samotné přijetí audio paketů ji nezaručuje.

V této implementaci byl ověřen skutečný `gpt-live-1`: průběžný hlasový vstup,
přepis, client delegation a jedna uložená fiktivní hůlka Modrostežka. Oddělený
krátký přehrávací test ověřil slyšitelný úvod se známou modrou nití a oznámení
potvrzeného výsledku bez další výroby. TTS bylo pouze zdrojem fiktivní uživatelské
řeči v automatickém testu; produktovou konverzaci obsluhuje GPT Live.

Jedna skutečná image-only oprava původní fiktivní hůlky Modrosteh přes
`gpt-image-1.5` zachovala WandId a zdrojový checksum, vytvořila v2 ready PNG,
a odmítla cizího vlastníka. Výřez byl prohlédnut na světlém i tmavém pozadí.
Browser testy pokrývají 320/390/1280px, 200% text, simulovanou soft klávesnici,
obnovu, proměnlivé výměny, reduced motion a samotný retry obrázku.

Živý API test není důkaz plynulosti na telefonu. Android policy je ověřená
samostatným Java harness; úplný Android Gradle a iOS/Swift běh na tomto hostu
nebyly provedeny. Skutečná telefonní soft klávesnice, safe areas a nativní
mikrofon zůstávají k ověření na zařízení. Produkční nasazení a fyzické akce
nejsou součástí úkolu.

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
