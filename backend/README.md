## Distributed tracing

Opt-in distributed tracing, privacy rules, local verification and rollout guidance:
[Honeycomb tracing](../docs/honeycomb-tracing.md). Existing request IDs and server logs remain available independently.

## Public production origin

The canonical public entry is `https://dildovander.de`; HTTP and `www` redirect
there. APIs and WebSockets share that origin. Loopback listeners and development
LAN hosts remain internal. The historical IP bootstrap commands below are retired
and must not be run against the domain deployment. Account identity uses RP
`dildovander.de` and exact allowed origin `https://dildovander.de`; keepsake links
use `Keepsake__Origin=https://dildovander.de`. Configure any separate web operator
with its exact `Show:AllowedOrigins` entry rather than a wildcard. Native signing
uses `KUFR_PASSKEY_RP_DOMAIN=dildovander.de`. The standalone log reader defaults
to the canonical URL and retains its `KUFR_BACKEND_URL` override.

# Backend kufru

## Hlas mobilní Kufrovy dílny

Zákaznická dílna `/play/wand` používá ElevenLabs Scribe pro český přepis,
stávající GPT provider pro text a rozhodnutí o výrobě a ElevenLabs pro hlas.
Prohlížeč stále používá chráněný jednorázový WebSocket ticket a PCM24; klíče
poskytovatelů zůstávají na serveru. Ticket vyžaduje uložený jednotný AI souhlas
verze 2 pro OpenAI i ElevenLabs; starší souhlas se jednou obnoví. Při dalším
hovoru se nový checkbox nezobrazuje. Rozmluvená odpověď zneplatní starší rozhodnutí
o výrobě. Stop ruší přehrávání, ale již přijatá výroba pokračuje.

| Serverové nastavení | Výchozí hodnota / význam |
| --- | --- |
| `OPENAI_API_KEY` | GPT rozhovor a výroba hůlky |
| `ELEVENLABS_API_KEY` | Serverové připojení ElevenLabs; chybějící klíč nevynutí návrat ke GPT-Live |
| `Wands__Voice__Provider` | `elevenlabs`; `gpt-live` je výslovná kompatibilní volba pro starší testy |
| `Wands__Voice__VoiceId` | Výchozí český hlas Zazy, `U48DQ1c9SVmD2BVCSiHL`; správce může uložit vlastní hlas Kufra |
| `Wands__Voice__Speed` | `1.0` (normální rychlost); povolený rozsah `0.7` až `1.2` |
| `Wands__Voice__Model` | `eleven_v4`; ostatní nastavení hlasu používají výchozí hodnoty poskytovatele |
| `Wands__Model` | Stávající volba GPT pro textový rozhovor a výrobu |
| `Keepsake__ElevenLabsArtworkFallback` | `true`; aktivní pouze s klíčem ElevenLabs a primárním OpenAI poskytovatelem |

Pokud OpenAI selže při tvorbě ilustrace uložené hůlky nebo vrátí neplatný
průhledný PNG, následuje nejvýše jeden pokus přes ElevenLabs Image API.
Používá `gpt-image-1.5` s průhledným pozadím a stejnou kontrolou PNG jako hlavní
cesta. Předává jen materiál, jádro a vizuální popis hůlky, ne profil hosta,
fotografii nebo přepis. Zrušení, ztráta oprávnění a odmítnutí obsahu nespouštějí
záložní pokus. Opakované čtení stavu jedné generace nevytváří další obrázek.

Pro zkoušení bez nasazení použij [místní hub](../docs/local-try-hub.md).
Správa `/settings/admin` obsahuje nezávislá ID hlasů Kufra a Barona.
`GET/PUT /app/v1/admin/actor-voices` používá oprávnění správce; zápis vyžaduje
aktuální ověření přístupovým klíčem, potvrzení a shodnou revizi. Nastavení
zůstává v `actor-voices.json` pod datovým adresářem identity. Kufr zachytí hlas
na začátku spojení, Baron na začátku zvukového požadavku; klíč cache obsahuje
zvolený hlas, takže změna nepřepíše již rozmluvenou repliku.

Všechna generování a editace obrázků používají společný `ImageVisualizer`.
Obrazový prompt obsahuje slovní popis, nikoli serializovaný profil nebo JSON
návrhu. Terra vytváří samostatný vizuální odstavec `designDescription`;
ilustrátor hůlky dostává tento popis, materiál a jádro. Ostatní obrazové větve
převádějí vlastní povolené vizuální údaje na prózu. Přenosový obal API nadále
používá JSON podle protokolu poskytovatele; pravidlo se týká obsahu promptu.

Offline kontrola: `dotnet run --project backend-tests/BackendTests.csproj -c Release -- --elevenlabs-workshop`
a `-- --wand-artwork` z kořene repozitáře. Tyto kontroly nevolají poskytovatele.
Protokoly: [Scribe Realtime](https://elevenlabs.io/docs/api-reference/speech-to-text/v-1-speech-to-text-realtime),
[streamovaný hlas](https://elevenlabs.io/docs/api-reference/text-to-speech/stream),
[Image API](https://elevenlabs.io/docs/api-reference/flows/image/create).

## Rickroll video

Set `Rickroll__FilePath` in the server's environment file to an absolute path to
an MP4 in durable storage outside Git checkouts, webroot and release folders.
The file must be readable by the backend service. Do not add the video to source
control, release packages or GitHub artifacts. Missing configuration or a missing
file disables the optional surprise; it does not block entry or gameplay.

`GET /app/v1/launch/rickroll` returns authenticated availability metadata. The
fixed `GET`/`HEAD /app/v1/launch/rickroll.mp4` route serves this non-confidential
asset publicly, including before launch, with byte ranges, conditional caching
and `video/mp4`. Requests never select an arbitrary file. Native clients stream
from their authenticated backend origin without copying video through the JSON
identity bridge. No YouTube scripts, embeds or third-party player are used.

The waiting screen preloads the video and starts it at 80% player volume only
when a foreground countdown crosses zero. Arriving, returning or restarting
after expiry follows normal entry. Browser/device restrictions can still block
audible autoplay or programmatic volume control.

An Owner/Administrator can enable the group option in the boss controls. Entering
the boss screen then atomically claims one playback per account across groups
and devices. Disabling and re-enabling does not reset claims. Claims commit
before playback, so a lost response or blocked player can skip the surprise
rather than replay it. Account deletion removes the claim. The toggle has its
own revision and does not change boss strength or combat state.

Focused validation: backend harness `--rickroll-media`, `--mobile-game-boss` and
`--application-updates`; frontend media, launch and boss specs. Set
`RICKROLL_TEST_MP4` to a local file outside the repository when running
`npm run test:launch:browser` to verify real MP4 playback without checking in the
asset. Publishing application code and restarting services remain separate
release operations from installing the media file.

## Limity požadavků účtu

`ApplicationIdentity:ReadRequestsPerMinute` nastavuje limit běžného čtení na
4 000 požadavků za minutu ve výchozím stavu (povolené rozmezí 10–12 000).
Čtení `GET`/`HEAD`, vyhrazený polling `account-poll` a ověřování účtu
`GET /app/v1/session` mají každý samostatný limit podle IP adresy. Vyčerpání
čtení nebo příkazů proto neblokuje ověření přihlášení. Uživatelé za stejnou
veřejnou IP adresou sdílejí příslušný limit.

`ApplicationIdentity:WebSocketRequestsPerMinute` nastavuje samostatný limit
WebSocket připojení: výchozích 4 000 pokusů za minutu, rozmezí 10–12 000.
HTTP upgrade a `CONNECT` používají tento limit; nečerpají limit čtení ani příkazů.
Obnovení jednorázového ticketu pro dílnu a Persona Lab má vlastní limit stejné
velikosti, aby nové připojení neblokoval nižší limit běžných příkazů.
Počet současně aktivních relací a jejich oprávnění se tím nemění.

Přihlášení a změny dat zachovávají nižší limit
`ApplicationIdentity:RequestsPerMinute`: výchozích 40 za minutu, rozmezí 10–120.
Odmítnutí vrací HTTP 429, kód `rate_limited` a hlavičku `Retry-After`.
Tyto limity nemění dobu platnosti relace ani samostatné limity herních API.
Offline HTTP regrese: `dotnet run --project backend-tests/BackendTests.csproj -- --identity-rate-limits`.

## Vlastník ve hře

Účet s rolí `Owner` má pevné zobrazované jméno `Olivander` a členství ve všech
hráčských skupinách. Uložené `LaunchGroupId` vybírá jeho aktuální hráčský kontext;
přepnutí neodebírá členství v ostatních skupinách. Ostatní účty nadále vyžadují
roli `Customer`, přiřazení ke skupině a platný přístup.

Owner-only `GET /app/v1/customer/groups` vrací `{ groups, groupId, accessRevision }`
a při chybějící volbě vybere první existující skupinu. `PUT` na
`/app/v1/customer/groups/active` přijímá `{ groupId, expectedRevision }`;
souběžná změna vrací `409`. Používá stávající ověření relace, originu a CSRF.
Owner může připravit postavu a první hůlku před otevřením skupiny. Samotná hra
a vyšší úrovně hůlky zachovávají podmínky spuštění a postupu.

Cílené HTTPS regrese spouští backend harness přes `--owner-player-http`.

[AI Persona Lab](Features/PersonaLab/README.md) poskytuje oddělené testování
postavy přihlášeným účtům, verzované instrukce, hodnocení a diagnostiku obou
podporovaných hlasových enginů.

## Show Pack

[Provoz a společné kontrakty](../docs/show-pack.md) rozšiřují současné DV01/DV10,
nikoli vlastnictví zařízení. `Features/Show` obsahuje deterministickou režii,
immutable finále svázané s opravdovým proposal ID, duel a příjem offline výsledků.
Paměť/kronika používají stávající evening store; identita a soukromé proroctví
stávající DV10 dokument. [HTTP rozšíření](../contracts/show-openapi-v1.json)
odděluje admin/device/public/duel-side scopes. Aditivní migrace probíhá při
otevření stejných SQLite; ruční archivní retence a nejisté provider pokusy zůstávají.
Z adresáře `` použij pinned SDK přes `global.json` a
`dotnet run --project backend-tests/BackendTests.csproj -- --show`.
Pro cross-origin webového operátora nastav přesný `Show:AllowedOrigins` seznam.
`Show:DuelCommentaryEnabled` má default true. Nastavením na `false` vypneš textové
Responses vyprávění a ponecháš deterministické komentáře; duely nikdy neotevírají
GPT Live ani hlasové přehrávání. Produkční textový poskytovatel potřebuje běžný
`OPENAI_API_KEY`; offline fixture konfigurace musí volbu výslovně vypnout.

Produkční rozhovor používá jedinou osobnost **DV01** a **GPT Live `gpt-live-1`**.
GPT Realtime 2.1 `gpt-realtime-2.1` zůstává neaktivní zdrojovou zálohou pro
obousměrný rozhovor. Samostatná syntéza hotového textu používá Realtime API
model `gpt-realtime-2.1-mini`; výsledkem zůstává WAV. Live tvoří běžnou řeč sám.
Luna rozhoduje o volbě hůlky, Terra obsluhuje textový dialog, výzkum a delší
autorské texty. Samostatná vision služba zpracovává fotografie.
Deterministické adaptéry jsou testovací pomůcky.

Výchozí modely aplikace jsou nastavené v `backend/appsettings.json`; odpovídající
proměnné prostředí s `__` místo `:` je mohou přepsat. Při nasazení mají přednost
existující explicitní provozní hodnoty.

| Úloha | Výchozí model | Konfigurace |
| --- | --- | --- |
| Textový dialog, výzkum a autorské texty | `gpt-6.1-terra` | dialog má vestavěný výchozí model; `Backend:Vision:Model`, `Keepsake:StoryModel`, `CustomerCharacters:Model`, `Wands:Model` |
| Úzké rozhodnutí Live a hráči Pod pečetí | `gpt-6-luna` | `Backend:GptLive:DecisionModel`, `SealedCase:AiPlayers:Model` |
| Obrázky portrétů a hůlek | `gpt-image-2.5-flare` | `CustomerCharacters:PortraitModel`, `Keepsake:ImageModel` |
| Plně duplexní rozhovor | `gpt-live-1` | `Backend:Conversation:LiveModel` |
| Syntéza hotového textu pro uložené odpovědi a krátké pokyny | `gpt-realtime-2.1-mini` | `ShowCueSpeechClient` |

Generování obrázků posílá `moderation=low`, včetně portrétů a průhledných hůlek;
parametr se ukládá také do provenance hůlky. Textové a hlasové požadavky tento
obrazový parametr nepřijímají. Starší volba hlasu `onyx` se při samostatné syntéze
překládá na podporovaný `cedar`.

Mobilní dílna vyhodnotí ustálený uložený přepis i bez delegace hlasového modelu.
Stejný přepis znovu automaticky nevyhodnocuje; počet rozhodnutí v jednom spojení
je omezený. Opravené odpovědi a aktuální oprávnění se kontrolují před přijetím
výroby. Odpojení nezahodí již rozběhnuté rozhodnutí, pokud jeho podklady a
oprávnění stále platí. Uložení hůlky a její obrázek mají samostatný stav;
neúspěšný obrázek nevyžaduje novou hůlku. Diagnostika odmítnutí uchovává HTTP
status, nikoli text těla odpovědi poskytovatele.

[Aktuální architektura a provozní kontrakt](../docs/autonomous-live-memory.md)
popisuje paměť, transport, migraci a akceptaci.
[privátní evidence](../docs/development-coordination.md) odděluje implementaci,
CI, nasazení a skutečný iPhone/kufr.

## Vlastnictví a API

ASP.NET Core/.NET 10 vlastní identitu rozhovoru, katalog, paměť a validované
návrhy. ESP32-S3 se připojuje přímo přes HTTPS, vlastní fyzickou sekvenci a
ověřuje dostupnost, revizi, kalibraci a Stop. Telefon není cloudový prostředník.
`GptLive` vlastní aktivní protokol; `Conversations` společný lifecycle. `Realtime` zůstává neaktivní záloha.
[Provisioning vlastního hlasu a fallback](../docs/dv01-voice.md) je pouze backendová operace.

`GET /app/v1/application-version` je veřejný před-přihlašovací endpoint pro
instalované aplikace. Vrací `ios` a `android`, každý s řetězcovými
`minimumBuild`, `latestBuild` a `updateUrl`; odpověď má `Cache-Control: no-store`.
`minimumBuild` určuje nejnižší podporovanou verzi a `latestBuild` nejnovější
doporučenou. iOS používá jeden až tři číselné komponenty, například `10.9.1`,
Android jeden číselný komponent odpovídající version code (nejvýše 2100000000).
Porovnání probíhá numericky po komponentách a kratší hodnoty se doplní nulami.
Konfigurace je v `ApplicationUpdates:Ios` a `ApplicationUpdates:Android`; oba
buildy jsou při vydání nejprve `"0"`. iOS odkazuje na `/ios/`. Android odkaz
zůstává prázdný, dokud není potvrzena veřejná instalační adresa; při nastavení
nenulové Android verze je nutné zadat absolutní HTTPS adresu. Aplikace provádí
vynucení aktualizace.

- `POST /v1/conversations` založí aplikační rozhovor idempotentně.
- `POST /v1/conversations/{id}/gpt-live` nebo `/realtime-live` otevře engine.
- `GET /v1/conversations/{id}/{gpt-live|realtime-live}/{streamId}/audio-stream`
  je autentizovaný WebSocket. PCM16 mono24kHz zprávy mají `inputId`, `sequence`,
  `audio` base64; ACK potvrzuje vstup, nikoli přehrání výstupu. Fronta je omezená,
  po překročení deadline klient skončí ve vysvětlené chybě bez replay mikrofonu.
- Fyzický audio klient může používat `/audio` se stejnou posloupností; během
  WebSocketu nelze přimíchat HTTP vstup. `/played` potvrzuje klientské dohrání.
- `DELETE /v1/conversations/{id}` ukončí provider a kontext; archiv zachová.
- Diagnostika a server modes vracejí `engines.schemaVersion=1`,
  `primaryEngine=gpt-live`, přesné modely, hlasy, tempo, audio vstup a kamery.
  `ready` je místní konfigurace; přístup účtu ověří až provider handshake.

Řetězený STT/dialog/TTS, Responses dialog, skriptovaný Live a oddělený Realtime
PTT jsou vyřazeny. `/turns`, `/opening`, `/transcriptions`, `/speech` a
`/realtime-turns` vracejí **410 `conversation_engine_upgrade_required`**.
Totéž platí pro změnu starých `dialogMode`/`voiceMode`. Aktualizuj nativní IPA;
webový bundle sám nenahradí instalovaný Swift bridge. Nový firmware textové
příkazy explicitně odmítá, hlasové enginy a offline ruční řízení zůstávají.

## Fotografie

`POST /v1/conversations/{id}/image` přijímá dekódovatelný JPEG do256KiB a640×640
bez EXIF/GPS. Korelační hlavičky: `Idempotency-Key`, `Client-Turn-Id`, `Boot-Id`,
`Generation`, `Device-Revision`, `Catalog-Version`. Aktuální klient přidává
`Image-Id` UUID, `Image-Source` (`phone`, `browser`, `suitcase`), `Captured-At`,
rostoucí `Image-Revision` a `Image-Consent: true` při výslovném přiložení.
Server kontroluje vlastníka/generaci; UUID nelze přebindovat jinému rozhovoru.

Fotografie může přibýt během hovoru. Vision má vlastní souběh; chyba nezruší
hlas. Live dostává ohraničené pozorování; neaktivní Realtime záloha uchovává historickou podporu `input_image`.
Text uvnitř fotografie je nedůvěryhodný. Kostým není identita ani souhlas
s osobním roastem. `PUT /costume-context` opraví popis. `DELETE /image` se
stejnou korelací a `Image-Revision` zneplatní i opožděný upload dané revize,
ponechá hlas běžet a zachová archiv. Explicitní smazání uloženého obsahu:
`DELETE /v1/admin/conversations/{id}/media?deviceId=...`. Správce se autentizuje
odděleně; aktivní provider obsahující smazané údaje se ukončí.

## Lokální vývoj a ověření

Soukromý `Backend__DatabasePath` patří mimo checkout, provider klíč pouze na
backend. `.env.example` se automaticky nenačítá. Bez `OPENAI_API_KEY` server
běží, oba enginy hlásí nedostupnost. Staré dialog/voice přepínače nic nezapínají.
OpenAI SDK2.14.0 obsluhuje Responses/Realtime; izolovaný Live používá oficiální
WS protokol. Úzké vision/decision/transcription schopnosti nejsou další engine.

```powershell
dotnet build backend-tests/BackendTests.csproj
dotnet run --project backend-tests/BackendTests.csproj -- --conversation-engines
dotnet run --project backend-tests/BackendTests.csproj -- --autonomous-memory
dotnet run --project backend-tests/BackendTests.csproj -- --memory
dotnet run --project backend-tests/BackendTests.csproj -- --image
dotnet run --project backend-tests/BackendTests.csproj -- --character
```

Fixture testy nevolají providera a nedokazují skutečný poslech/kameru/pohony.
Placený test vyžaduje autorizaci. Starý dialogue smoke není akceptace nových enginů.

Při změně NuGet závislostí regeneruj oba lockfiles společně přes
`dotnet restore backend-tests/BackendTests.csproj -p:SelfContained=true --force-evaluate`.
Zachovej deklarované `RuntimeIdentifiers` (`linux-x64;win-x64`) a potom ověř
stejný příkaz s `--locked-mode` místo `--force-evaluate`. Microsoft.Data.Sqlite
je aktuálně připnutý na10.0.13; OpenAI SDK zůstává2.14.0.
Dependabot zahrnuje oba projekty; [automatizace generovaných závislostí](../docs/ci-cd.md#generovane-zavislosti-v-dependabot-pr)
oba lockfiles přegeneruje a commitne do jeho větve ještě před finálním CI.

## Nasazení a rollback

Používej existující CI a artefakt ověřeného Git tree. Před první aktivací udělej
[soukromou konzistentní zálohu a migraci](../docs/autonomous-live-memory.md#aktivace-ab-a-rollback).
Databáze se neresetuje, nové tabulky vznikají aditivně. Záloha není GitHub
artefakt. Starší release s automatickou retencí nesmí zpracovat jedinou kopii archivu.

Runner `[self-hosted,Linux,X64,kufr-backend]`, existující `ci.yml` a soukromý
deploy workflow používají ověřený `build_run_id`. Cesty:
`/opt/kufr-backend/current`, `/opt/kufr-backend/releases`,
`/etc/kufr-backend/backend.env`, `/var/lib/kufr-backend`; služba
`kufr-backend.service`, loopback `127.0.0.1:5080`. Před změnou konfiguraci
zálohuj; po aktivaci ověř službu, health, capabilities a data. Diagnostika:
[codex návod](codex/README.md). Rollback vrací předchozí kompatibilní release,
nikdy neobnovuje třetí engine v aktuálním runtime.

## Podepsaná firmware distribuce (D158)

Veřejné read-only `GET /v1/firmware/manifests/{channel}.json` a
`GET /v1/firmware/artifacts/{sha256}.bin` nevyžadují device token. Autoritu
určuje P-256 podpis provisionovaný na ESP, nikoli samotný checksum.
`Backend:Firmware:Directory` je absolutní admin-owned adresář; neplatná
konfigurace/release vrací 404. Shared concurrency limit 4, queue 0, 429.
Root-only publikace ověřeného CI artefaktu udržuje podpisové klíče mimo
runner/source/image. [Kontrakt a přesný deployment](../docs/firmware-ota.md).



### Veřejné HTTPS na IP adrese

Workflow **Kufr backend IP HTTPS** na `main` má ruční režim `stage`, který
na serverovém runneru připraví přesně commitované soubory z
`deploy/ip-https/` v soukromém adresáři a vypíše cestu a SHA-256. Skript se
automaticky nespustí s root právy. Správce ověří hash a spustí jej v root
shellu jako `sudo bash CESTA/bootstrap-ip-https.sh 2.28.202.220`.
Předpoklady: běžící loopback backend, volné porty 80/443 a příchozí TCP
80/443 povolené i ve firewallu poskytovatele. Skript odmítne cizí Nginx site,
nainstaluje Nginx a Certbot 5.4+ ze snapu, vystaví veřejně důvěryhodný
krátkodobý certifikát s IP SAN přes HTTP-01 webroot a zapne HTTPS proxy.
Port 80 slouží obnově certifikátu a jinak přesměrovává na HTTPS. Nginx
nikdy neposílá provoz přímo na veřejný port backendu.

`kufr-cert-renew.timer` kontroluje obnovu dvakrát denně; deploy hook po
obnově otestuje a znovu načte Nginx. Bootstrap ověří lokální důvěru TLS,
IP SAN a zkušební obnovu. Režim workflow `verify` z GitHub runneru ověří
veřejné `https://IP/healthz`, důvěru IP certifikátu, alespoň 48 hodin
platnosti a HTTP přesměrování. Stejná kontrola běží denně s repository
variable `KUFR_BACKEND_PUBLIC_IPV4`. Bez úspěšného veřejného běhu se B4
nepovažuje za ověřené. Certifikát je krátkodobý, proto je průběžná obnova
součástí provozu. Při chybě lze zastavit jen Nginx (`systemctl stop nginx`);
backend zůstane dostupný na loopbacku pro runner/deploy diagnostiku.

### Safari instalace iOS aplikace

Po ověření veřejného HTTPS připrav režimem `stage` téhož workflow aktuální
soubory a jako root spusť `sudo bash CESTA/install-ios-host.sh 2.28.202.220
github-runner`. Skript přidá do vlastního Nginx site pouze statickou cestu
`/ios/`, vytvoří stránku „podepsaná verze se připravuje“ a při chybě
konfigurace vrátí předchozí site. Adresář `/var/www/kufr-ios` vlastní
serverový runner, který smí publikovat pouze statické soubory. Přístup k
backendovým secrets ani k portu 5080 stránka nemá.

Po úspěšném Ad Hoc podpisu **Build signed Kufr IPA** samostatné workflow
**Deploy Kufr IPA** se vstupem ID podepsaného buildu nahraje do tohoto adresáře
verzovanou IPA, `manifest.plist`, SHA-256 a instalační stránku. Změna
hlavní stránky proběhne atomicky až po uložení celé verze. Samostatný
job na témže serverovém runneru ověří přes veřejné HTTPS stránku, manifest, MIME typy,
staženou IPA a checksum. Instalaci `itms-services` a spuštění může potvrdit
jen registrovaný iPhone v Safari; serverový test to nenahrazuje a neprokazuje
dostupnost z jiné sítě. Soubory
pod `/ios/` jsou veřejně čitelné, proto IPA nesmí obsahovat tajemství.

Tarif Responses řídí `OpenAiServiceTierPolicy` podle operace a každý požadavek ho uvádí explicitně, aby nepřebíral placené nastavení projektu:

| Operace / veřejný vstup | Tarif Responses |
| --- | --- |
| Výzkum postavy po `PUT /app/v1/customer/character` nebo `PUT /app/v1/prelaunch/character`, opravě přes `/correction` a povoleném opakování | `fast` |
| Přivítání a volitelný průběžný text při přípravě portrétu (také přes `POST /app/v1/prelaunch/character/retry` a oprávněný `POST /app/v1/admin/launch/personalization-preview`) | `fast` |
| Rozhovor dílny: `POST /app/v1/customer/wand/workshop/sessions` a `/{id}/messages` | `fast` |
| Návrh hůlky po `POST /app/v1/customer/wand/workshop/sessions/{id}/finish` a povoleném opakování | `fast` |
| Příběh památky (`OpenAiKeepsakeProvider`), běžná hra, komentáře souboje, analýza kostýmu, Live rozhodnutí a syntetický adaptér zapečetěného případu | `default` (Standard) |

Fast požadavky přijímají vrácené `fast`, dokumentovaný starší alias `priority` nebo legitimní snížení na `default`. Chybějící či neznámý tarif se odmítne. Standard požadavky v rozpočtovaných operacích a příběhu památky nadále odmítají jiný tarif. Nevzniká automatické placené opakování. Modely, souhlasy a pravidla vlastnictví se nemění.

Sdílená kalkulace rozpočtu pro Terra/Sol oceňuje skutečný vrácený tarif: Fast používá 2× Standard sazby, snížení na Standard používá Standard sazby, zaokrouhlení proběhne jednou. Stávající rezervace v rozpočtovaných operacích pokrývají i Fast při jejich limitech vstupu a výstupu; neočekávaný tarif ponechává jejich rezervaci nevypořádanou. Příprava postavy a hůlky zachovává své dosavadní tokenové audity a limity; tento přepínač jim nezavádí nový peněžní rozpočet. Interní částky jsou konzervativní odhad ceníku, nikoliv fakturace OpenAI.

Samostatné Images API pro portréty a ilustrace hůlek nemá v dokumentovaném schématu parametr `service_tier`; změna proto zrychluje textovou přípravu a neslibuje rychlejší generování obrázků. Realtime, hlas a embeddingy se tímto tarifem neřídí. Podklady ověřené 2026-10-29: [Fast a snížení tarifu](https://developers.openai.com/api/docs/guides/priority-processing), [ceník podporovaných modelů](https://developers.openai.com/api/docs/pricing), [parametry Images](https://developers.openai.com/api/reference/resources/images/methods/generate). Živé placené ověření nebylo součástí změny.

Úplný postup a omezení opakovaného použití artefaktů jsou v [CI/CD návodu](../docs/ci-cd.md).

## Serverové logy a společný debug D118

Backend ukládá technické události do oddělené SQLite, výchozí historie 24 h
a 5000 záznamů. `GET /v1/admin/logs` a `/v1/admin/logs/status` používají
credential správce nebo samostatný reader token; device access nestačí.
Každý požadavek vrací `X-Request-Id`, Realtime zapisuje i fáze/chyby uvnitř
streamu. Úspěšné GET a audio uploady se vynechávají. Bez textů/audio/tajemství.

[Codex integrace a API](codex/README.md) popisují kurzory, filtry, úložiště,
MCP, společný postup s iOS a GitHub workflow **Kufr server logs**. Pro přímé
MCP lze v `backend-production` přidat volitelný secret `LOG_READER_CREDENTIAL`;
deploy uloží jeho hash. Bez něj funguje iOS/admin a GitHub čtení přes existující
admin secret. Čtení neprovádí provider volání ani změny serveru.

## Skupiny pozvánek a předstartovní přivítání

### Samoobslužná beta a členství

Instalační pozvánka TestFlight/Firebase stačí pro vstup do aplikace. Neprokazuje
identitu účastníka: po dobu otevřeného náboru je beta samoobslužná. Nový účet
získá pouze roli `Customer`. Pro malou známou skupinu po přijetí všech nábor zavřete.

Majitel v existující správě vybere nebo vytvoří výchozí náborovou skupinu a výslovně
povolí přijímání. Tato skupina automaticky přebírá globální produkční termín a stav
odemčení; členství s rolí Beta na tom nic nemění. Poté pošle pouze instalaci.
Uzavření náboru neblokuje aktivní členy. V přehledu účastníků lze
jednomu účtu odebrat nebo obnovit přístup; obnova vrátí stejná data i při zavřeném
náboru a sama nespustí zrušenou práci. Shodná jména rozlišuje ID účtu.

Konfigurace náboru začíná zavřená a migrace ani restart ji neotevřou.
Výchozí skupina se při dosažení produkčního termínu otevře automaticky. Posunutí
produkčního termínu do budoucnosti ji vrátí do čekání i po předchozím otevření.
Zapnutá produkční brána bez termínu zůstává uzavřená; vypnutá brána nebo výslovné
produkční odemčení ji otevře ihned. Nábor je nezávislý a změna plánu jej nezapíná.
Konfigurace i výchozí skupina se mění v jedné transakci. Start serveru jednorázově
srovná i dříve uložený termín bez nutnosti nového uložení správcem; opakování beze
změny neposouvá revizi skupiny ani nepřidává audit. Ostatní skupiny mají vlastní odpočty.
Změna výchozí skupiny nepřesouvá přijaté účty. Passkey registrace váže skupinu
a revizi pravidel při zahájení a znovu je ověřuje před založením účtu a členství.
Pevné univerzum se ukládá s přijetím a server je vynucuje i při pozdějších zápisech.

Veřejný stav vrací `GET /app/v1/enrollment/default` (`available`, `fixedUniverse`);
passkey používá stávající `POST /app/v1/customer/enrollment/options` a `/complete`.
Přihlášený účet čte `GET /app/v1/customer/access` se stavem
`unassigned|active|revoked` a revizí přístupu. Nepřiřazený účet se může výslovně
přihlásit přes `POST /app/v1/enrollment/default/admit`; čtení členství nevytváří.
Správa používá `GET/PUT /app/v1/admin/launch/beta-admission`, pomocné
`POST /app/v1/admin/launch/beta-admission/group` a
`GET /app/v1/admin/launch/groups/{id}/members`. Přístup účastníka mění
`PUT /app/v1/admin/launch/groups/{id}/members/{userId}` s
`{expectedRevision,active}`. Zápisy zachovávají CSRF/origin, recent passkey,
správcovská oprávnění a potvrzovací hlavičku `X-DV-Confirm: yes`.

Revokace ponechává členství, účet i data. Onboarding zobrazí odebraný přístup
a server odmítne další přípravu postavy. Rozpracované obohacení a portrét se
zruší a pozdní výsledky nesmějí publikovat. Přihlášení, obnova a vlastní
export/smazání zůstávají dostupné. Tato změna se soustředí na beta onboarding;
nerozšiřuje runtime generování hůlek, památek ani rozhovorů.

Nová postava vyžaduje výslovný AI souhlas i na obecném character API; bez něj
nevzniká ani profil ve stavu `saved`. Lokální rozepsaný formulář není postava.
Existující historická data se nemažou. Souhlas s AI není souhlas s publikací,
mikrofonem ani osobní pamětí.

Před nasazením je potřeba standardně zazálohovat data a aplikovat aditivní
Identity migraci obvyklým release postupem. Nastavení skupiny a povolení náboru
jsou samostatné správcovské kroky; lokální testy je v produkci neprovedou.

`GET/POST /app/v1/admin/launch/groups` a
`PUT /app/v1/admin/launch/groups/{id}` spravují samostatné skupiny
`{id,name,launchAt,revision,countdownDurationSeconds,forceStartAtEnd,countdownConfirmedAt,openedAt}`.
Zápis termínu používá očekávanou revizi;
u nezávislé skupiny `launchAt=null` drží vstup uzavřený. API pro pozvánky přijímá volitelné
`launchGroupId`. Členství se uloží pouze při skutečném založení nového účtu
z dané pozvánky; obnova účtu jej nepřepisuje. Správa je chráněna stávajícími
administrátorskými/operačními kontrolami.

### Čekárna a potvrzené odpočty skupin

Správa čekárny zobrazuje přijaté zákaznické účty, které čekají na otevření své
skupiny, i nepřiřazené účty a odebraný přístup. Nejde o ukazatel okamžité online
přítomnosti. Přesun je výslovná správcovská akce se zdrojovou skupinou a očekávanou
revizí členství; zachovává účet, postavu, passkeys, souhlasy, univerzum i případnou
revokaci. Výchozí náborová skupina se tím nemění. Již otevřená zdrojová skupina
nepatří do této čekárny. Přesun do otevřené cílové skupiny umožní aktivnímu členovi
vstup ihned; odebranému členovi přístup neobnovuje.

Ostatní skupiny mají vlastní návrh délky odpočtu a volbu `forceStartAtEnd`.
Výchozí náborové skupině nelze návrh uložit, potvrdit ani ručně zahájit: server vrací
`409 launch_group_uses_production_schedule`; změna názvu a obsahu zůstává povolená.
Její termín upravuje globální produkční kalendář přes `PUT /app/v1/admin/launch`.

Pro nezávislé skupiny platí:

1. Uložení návrhu odpočet nespouští; host vidí „Brzy“.
2. Potvrzení spustí celou nastavenou délku od serverového času potvrzení.
3. S volbou Force se večer otevře automaticky až po skončení potvrzeného odpočtu.
4. Bez Force zůstává po skončení odpočtu vstup uzavřený a host vidí „Brzy“, dokud
   správce výslovně nepotvrdí zahájení večera.

Force nikdy neznamená okamžité otevření při uložení návrhu nebo přeskočení
potvrzení odpočtu. Globální odemčení nepotlačuje potvrzení nezávislých skupin ani revokaci.
Změna názvu skupiny běžící odpočet nemění. U nezávislých skupin migrace ani restart
nic automaticky nepotvrzují; starší uložený termín se převádí na nepotvrzený návrh délky od
poslední změny plánu (nejméně jedna sekunda), nikoli na povolení otevřít skupinu.

`GET /app/v1/admin/launch/waiting-room` vrací soukromý přehled pro správu
ve stránkách po 100 účtech jako `{members,nextCursor}`. Další stránka používá
`?after=<nextCursor>`; aplikace načte všechny stránky před povolením změn.
`PUT /app/v1/admin/launch/waiting-room/{userId}/group` přijímá
`{expectedMembershipRevision,expectedSourceGroupId,targetGroupId}`.
Návrh odpočtu ukládá `PUT /app/v1/admin/launch/groups/{id}/countdown` s
`{expectedRevision,durationSeconds,forceStartAtEnd}`. Potvrzení používá
`POST /app/v1/admin/launch/groups/{id}/countdown/confirm` a ruční zahájení po
skončení odpočtu `POST /app/v1/admin/launch/groups/{id}/start`; obě akce posílají
`{expectedRevision}`. Zůstávají platné dosavadní CSRF/origin, správcovské role,
recent-passkey, potvrzení a audit. Veřejné `/app/v1/launch` vrací účinný čas až
po potvrzení odpočtu; neověřený nebo ukončený neotevřený odpočet neodemyká API.

EF migrace `20261026045830_AccountLaunchGroupMembership` přidává nullable
členství účtům a pozvánkám. Nepřiřazené starší účty používají dosavadní plán.
Pro přiřazený účet je autoritou potvrzený odpočet a stav otevření jeho skupiny,
i když je globální plán otevřený; neexistující skupina zůstává uzavřená. Výjimky staff/tester
a ostatní audience/capability/resource oprávnění zůstávají samostatné.

Prelaunch artwork vrací volitelné `welcome:{joke,identity,teaser}` vedle
kompatibilního `finalMessage`. ID ověřených faktů, zdroje a povolené téma
zůstávají interní. Text i ilustrace používají stávající souhlas, budgety a
omezené retries; chyba textu ukládá stabilní fallback a nebrání obrázku.
Generování portrétu používá `moderation=low`. Když obrázek selže, worker zkusí
Wikimedia Commons bez dalšího placeného AI požadavku nebo nového API klíče.
Vyhledává jen fikční jméno, svět a případnou verzi; osobní popis ani údaje účtu
neodesílá. Přijímá pouze odpovídající obrázky s CC0, public domain nebo CC BY/CC BY-SA
licencí. Stažení má limit 8 MiB, kontrolu veřejných Wikimedia adres a dekódovaných
rozměrů; výstup se ořízne do portrétu a znovu zakóduje jako WebP.
Obrázek zůstává za stávající autentizovanou URL. Volitelné
`artwork.source:{url,title,author,license,licenseUrl}` ukládá nullable `source_json`
vedle obrázku v SQLite a aplikace zobrazí zdroj, autora, licenci a údaj o úpravě.
Starší portréty zůstávají beze změny. Hledání má vlastní 20s limit v rámci
240s lease a respektuje změnu revize, smazání a zrušení práce účtu. Bez vhodného
nálezu zůstává dosavadní stav chyby a ruční opakování.
Cílené kontroly: `--customer-profiles`, `--character-portraits`,
`--prelaunch-http`, `--launch-policy` v konzolovém `backend-tests` executable.
