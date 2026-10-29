# Místní zkoušení rozhovorů

Spusť z kořene `DildoVander-app` v PowerShellu 7.4 nebo novějším (`pwsh`):

```powershell
Set-Location mobilni-aplikace
npm ci
Set-Location ..
.\devtools\local-try-hub\Start-LocalTryHub.ps1
```

Launcher otevře `https://localhost:5173/local-try-hub`. Hlavní vstup vede do
`/play/wand`, zákaznické Kufrovy dílny používané mobilní aplikací. Její skutečný
backend a hlasový transport zkoušej právě tam. AI Persona Lab je oddělené
prostředí pro DV01 a jeho GPT Live nastavení; neověřuje zákaznický prompt ani
chování mobilní dílny.

Pro třetí volbu **Speech Engine + GPT** spusť:

```powershell
.\devtools\local-try-hub\Start-LocalTryHub.ps1 -SpeechEngineTunnel
```

ElevenLabs potřebuje veřejný serverový WebSocket. Tato volba stáhne oficiální
`cloudflared` 2026.10.6 do místních dat a před každým spuštěním ověří připnutý
SHA-256. Dočasný tunel Cloudflare vede pouze na oddělený loopback gateway
na portu 5083. Ten propustí jen WebSocket callback Speech Engine s dokladem
ElevenLabs; registrace, účet, místní přihlašovací schopnost i ostatní API vrací
404. Vlastní backend ověřuje podpis JWT a jednorázovou schopnost konkrétního
hovoru. Zvuk proudí mezi prohlížečem a ElevenLabs přes WebRTC; tunel přenáší
serverové přepisy a odpovědi. Ctrl+C ukončí i gateway a tunel. Bez tohoto
parametru se žádný tunel nespouští a třetí volba vysvětlí chybějící callback.
Místo tunelu lze předat vlastní `-SpeechEnginePublicOrigin https://...`;
na této adrese musí být dostupná pouze správně směrovaná cesta callbacku.

Speech Engine používá oficiální prohlížečový SDK `@elevenlabs/client`, hlas
Kufra vybraný správcem, `eleven_v4`, češtinu a rychlost 1.0. Terra vytváří
stejné odpovědi se stejnými prompty a Fast; reasoning je výchozí `none` a lze
ho změnit v administraci. Výrobu
stále potvrzuje náš backend. Předběžný přepis klienta je pouze náhled.
Ukládá se až potvrzená replika z podepsaného serverového spojení. Každý hovor
má vlastní dočasný prostředek ElevenLabs bez nahrávání, s retencí
nastavenou na 0 dní a limitem 180 sekund. Při ukončení se maže; neúspěšná smazání se opakují
podle odolné fronty `identity/speech-engine-cleanup.json`, i po restartu.
Stávající ElevenLabs Scribe + TTS i GPT-Live zůstávají samostatné volby.
První repliku Speech Engine předá přes oficiální `overrides.agent.firstMessage`;
backend ji uloží jednou a při opakovaném připojení znovu použije poslední
uloženou repliku. Následující odpovědi proudí přes upstream v rámci skutečného
tahu hosta. Při výrobě zůstane jeho odpověď otevřená až do uloženého výsledku.

## Místní účet a data

Backend používá samostatná data pod
`%LOCALAPPDATA%\DildoVander\local-try-hub`: databázi aplikace, databázi identity
a klíče ochrany dat. Launcher nepoužívá ani nemění produkční databázi či
nastavení identity. Tlačítko na rozcestníku připraví běžné místní sezení pro
vyhrazený testovací účet Customer + Tester. Registrace, pozvánka ani přístupový
klíč nejsou potřeba. Účet nemá oprávnění Owner ani Administrator a patří pouze
do oddělené místní databáze. Pokud už je místní účet přihlášený, hub ho zachová.

Před prvním rozhovorem potvrď zpracování u poskytovatele a založ místní postavu.
Tlačítko **Resetovat testovací postavu** na hubu načte aktuální místní postavu
a vyžádá si potvrzení. Reset odstraní její profil, hůlku a navázaný postup přes
běžné rozhraní postavy; uložené prompty a volba hlasu zůstanou zachované.
Hub tyto souhlasy neuděluje automaticky. Hůlka se ukládá do místní databáze.
Pokud místní vstup selže, restartuj spouštěč; běžný samostatný Vite server nemá
potřebné místní oprávnění.

Přímý vstup je dostupný pouze při explicitně zapnutém místním vývojovém režimu,
s izolovanými cestami k datům a loopback připojením. Spouštěč pro každý běh vytvoří
náhodné oprávnění, které předá pouze backendu a Vite proxy; nedostává se do
prohlížečového kódu ani URL. Produkční přihlášení tento režim nemění.

Pro HTTPS použije launcher existující důvěryhodný .NET vývojový certifikát pro
`localhost`; kopii předá Vite pouze v paměti běžícího procesu. Žádný soubor
certifikátu nevytváří. Certifikát ani systémovou důvěru sám nevytváří a nemění.
Pokud žádný důvěryhodný certifikát
není, launcher skončí s instrukcí `dotnet dev-certs https --trust`; tento příkaz
spusť samostatně jen pokud chceš důvěru certifikátu přidat do svého systému.

## Poskytovatel a provoz

Jde o skutečnou místní aplikaci a backend, ne o falešný rozhovor. Zákaznická
Kufrova dílna používá OpenAI pro textový rozhovor a výrobu hůlky; živý hlas
přepisuje ElevenLabs Scribe a hlasový výstup vytváří ElevenLabs. Launcher přebírá
jen klíče `OPENAI_API_KEY` a `ELEVENLABS_API_KEY` z procesu, nebo z uživatelských
či systémových proměnných prostředí, pokud v procesu chybí. Klíče nevypisuje ani
nekopíruje do souborů.
`OPENAI_API_KEY` je potřeba pro spuštění skutečného backendu; bez něj launcher
skončí s pokynem k nastavení proměnné. Bez `ELEVENLABS_API_KEY` zůstane dostupný
psaný rozhovor, ale hlas a záložní kresba hůlky nebudou fungovat.
Místní backend má zapnutou výrobu hůlky a ukládá její data do oddělené složky
`keepsakes`. Používá model příběhu `gpt-6.1-terra` a model kresby `gpt-image-2.5-flare`;
pokud kresba uložené hůlky selže a je dostupný klíč ElevenLabs, může se zkusit
záložní kresba přes ElevenLabs (`gpt-image-1.5`) s průhledným PNG. Tato záloha
platí jen pro kresbu hůlky, kterou uživatel právě zkouší. Fotografie, profil ani
přepis rozhovoru se jí neposílají. Výchozí hlas je Zazy (`U48DQ1c9SVmD2BVCSiHL`), model `eleven_v4`,
normální rychlost `1.0` a ostatní nastavení hlasu ponechaná na výchozích hodnotách
poskytovatele. Odpovědi se průběžně převádějí na hlas; mikrofon zůstává aktivní
i během přehrávání, takže host může Kufrovi skočit do řeči. V dílně lze měnit
hlasitost a tichý hlas se při přehrávání vyrovnává s omezením špiček.

Na rozcestníku lze místně přepnout hlasového poskytovatele mezi **Speech Engine
+ GPT**, **ElevenLabs v4 + GPT** a **GPT-Live** a upravit základní prompty pro
GPT backend a GPT-Live. S veřejným callbackem je výchozí Speech Engine; bez něj
launcher nastaví původní ElevenLabs. Již uložená volba se zachová.
Uložení se zapisuje do `%LOCALAPPDATA%\DildoVander\local-try-hub\experiments.json`;
platí jen pro toto místní prostředí a použije se při dalším připojení nebo
požadavku. Změny neovlivní právě probíhající rozhovor ani produkci. Oba prompty
mají limit 12 000 znaků. Základní prompt se připojuje k aktuálnímu kontextu běhu;
jeho systémová pravidla pro oprávnění a dostupné akce zůstávají beze změny.
Hlas i psaný rozhovor používají uložený jednotný souhlas s OpenAI a ElevenLabs
z registrace nebo jeho jednorázového obnovení. Před dalšími hovory se souhlas
znovu nevyžaduje. Verze 1 pokrývala jen OpenAI; server ji automaticky nerozšiřuje.
Živý přepis ukazuje rozpoznaná slova během řeči, včetně pozdějších oprav od
Scribe. Texty hosta a Kufra jsou oddělené. Odpověď GPT se doplňuje průběžně,
zatímco hlas dál streamuje. Návrhy přepisu se do podkladů pro výrobu dostanou
až po potvrzení rozpoznávačem; rozmluvená odpověď zneplatní starší návrh výroby.

Výchozí GPT prompt je uložený v `backend/Features/Wands/Prompts/dialogue.cs-CZ.txt`
a vložený do backendu jako prostředek sestavení. Předloha přednesu je v sousedním
`speech.cs-CZ.txt` a hub ji zobrazuje pro kontrolu. TTS API nepřijímá samostatný
instrukční prompt: nepřipojujeme jej k mluvenému textu. Používáme schválené
nastavení hlasu vybraného správcem / čeština / rychlost 1.0 a `previous_text` pro návaznost úseků
téže repliky. Nová replika začíná bez předchozího TTS kontextu.
Textové GPT `gpt-6.1-terra` pro rozhovor i výrobu používá `service_tier: fast`.
Správce může v `/settings/admin` měnit hlas Kufra a Barona nezávisle. Tyto
volby se ukládají v místních datech identity; testovací účet Customer + Tester
nezískává správní oprávnění samotným otevřením hubu.
Příprava místního účtu nic poskytovateli neposílá. Založení postavy, odeslání
zprávy nebo spuštění hlasu může volat skutečného poskytovatele a stát peníze.
Žádné takové volání při spuštění launcheru neprobíhá. Chybí-li některý klíč,
launcher jej ohlásí podle názvu; text a hlas mohou vyžadovat různé klíče.

Frontend naslouchá na `127.0.0.1:5173`, backend na `127.0.0.1:5082`. Vite
předává `/app/v1`, včetně WebSocketu, místnímu backendu a předává HTTPS původ
pro ochranu přihlášení. Skript předá backendu vlastní izolované proměnné
prostředí, databázi, identitu a telemetrii; frontend žádné klíče poskytovatelů
nedostane. Klíč v procesu má přednost před hodnotou User, ta před hodnotou
Machine. Prostředí spuštěného PowerShellu se nemění. Produkční soubory
konfigurace ani provozní prostředí se nenačítají.

Obě služby ukonči pomocí **Ctrl+C** v PowerShellu. Běhové logy jsou v
`%TEMP%\DildoVander\local-try-hub`; před sdílením je zkontroluj.

Požadavky: PowerShell 7.4 nebo novější (`pwsh`), .NET SDK, důvěryhodný .NET HTTPS vývojový
certifikát pro `localhost`, Node.js podle `mobilni-aplikace/.nvmrc` a balíčky
instalované příkazem `npm ci`. Porty 5082 a 5173 musí být volné.
