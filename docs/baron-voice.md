# Baron: text, hlas a herní kontext

Baron Šarlatán reaguje při zahájení pokusu, po odehrané výměně a při výsledku.
GPT dostane fikční jméno a svět hráče, aktuální doložené charakterové podklady,
navázanou hůlku a skutečně odehraná kola, energii, zásoby a fázi souboje.
Nedostává přihlašovací údaje, soukromé údaje hosta, náhodný seed ani příští
tajnou volbu Barona. Text ani hlas nesmějí ovlivnit pravidla, výsledek nebo XP.

Generování běží odděleně od herního tahu. Server kontroluje přihlášení, skupinu,
přístup k večeru a vlastnictví pokusu. Vstup tvoří pouze serverový stav; klient
posílá očekávanou revizi. Odpověď z jiného kola, změněného profilu, zrušeného
pokusu nebo zrušeného členství se nepoužije.

Text a MP3 se ukládají do SQLite pro právě aktuální kontext pokusu. Obnovení
stránky je znovu negeneruje; nový tah nahradí předchozí cache. Změna hlasu,
modelu nebo verze hlasového podání zneplatní pouze nahrávku; slova zůstanou
stejná i pro již otevřenou obrazovku. Krátký pronájem
v SQLite brání současnému generování stejné repliky, včetně více instancí serveru.
Při smazání účtu se smažou také tyto texty a nahrávky. Výpadek poskytovatele
nezastaví hru. Bez ElevenLabs klíče zůstávají dostupné textové repliky.

## Místní nastavení

Z kořene repozitáře:

```powershell
npm ci
npx -y @elevenlabs/cli@latest onboard init
npm run baron:check
npm run backend:dev
```

Onboarding otevře přihlášení a uloží klíč do ignorovaného `.env.local`. Klíč
nepatří do chatu, zdrojových souborů ani klientské aplikace. `baron:check` používá
oficiální `@elevenlabs/elevenlabs-js` SDK pouze pro kontrolu přístupu k hlasu,
nevytváří řeč. `backend:dev` načte `.env.local` pomocí Node `--env-file`
a předá prostředí backendu. Existující `OPENAI_API_KEY` z prostředí se zachová.
Node 22 nebo novější je potřeba pro tyto místní nástroje.

Backend používá oficiální ElevenLabs HTTP rozhraní přes `HttpClient`; produkční
.NET proces tedy nepotřebuje Node ani instalaci npm balíčků. Produkce dodává
proměnné přes svůj existující bezpečný service environment; tento krok není
nasazení ani změna produkčních tajemství. `.env.local` zůstává v tomto worktree;
nikam se automaticky nekopíruje a nesmí se při úklidu worktree ztratit.

| Proměnná | Účel |
| --- | --- |
| `OPENAI_API_KEY` | Stávající klíč pro generování textu |
| `ELEVENLABS_API_KEY` | Klíč pouze pro serverovou syntézu |
| `Game__BossVoice__VoiceId` | Uživatelův vybraný Baronův hlas `A4QaWrc6j9wnj1l191pm` |
| `Game__BossVoice__Model` | `eleven_v4`; jiný model server odmítne, aby se hlasové značky nečetly nahlas |

Český text zpracovává Eleven v4. Dostupnost vybraného hlasu ověřuje
`baron:check`; kvalitu českého přednesu ověří až živý poslech. Případnou změnu
hlasu nastav v privátním prostředí.

## Emoce podle souboje

GPT dostane kromě faktů i směr přednesu, aby mu odpovídala samotná replika.
Herní server přidá značky až k hlasové verzi; text na obrazovce je bez nich.
Značky pocházejí z pevného seznamu, nikoli z hráčova profilu či instrukcí
v generovaném textu. Do hlasu se neposílají scénické efekty, hudba ani SSML.

| Stav | Baronův přednes |
| --- | --- |
| Začátek souboje | Povýšený `[haughty]` |
| Baron právě zasáhl hráče | Povýšený se smíchem `[haughty] [laughing]` |
| Hráč právě zasáhl Barona | Naštvaný `[angry]` |
| Baronovi zbývá nejvýše jedna energie | Zoufalý `[desperate]`, má přednost před vztekem |
| Nerozhodná výměna | Sarkastický `[sarcastic]` |
| Skutečná porážka Barona | Zoufalý s povzdechem `[desperate] [sighs]` |
| Skutečné vítězství Barona | Povýšený smích `[haughty] [laughing]` |

O náladě rozhodují pouze odehrané tahy a zveřejněný výsledek, nikdy tajný
příští tah. Hlas nesmí prohlašovat porážku za vítězství. Pro v4 se posílá jen
`stability` a `similarity_boost`; konkrétní účinek značek je potřeba poslechnout.

## Přehrávání a API

Host zapne Baronův hlas výslovným tlačítkem. Prohlížeč se při tom odemkne pro
zvuk; mikrofon se nepoužívá. Nové kolo, ztlumení, skrytí stránky a odchod ze
souboje přeruší přehrávání i staré požadavky na zvuk. Selhání zvuku nechá text
viditelný. Automaticky se přehrává pouze poslední aktuální replika.

- `POST /app/v1/game/boss/attempts/{id}/narration`, tělo `{ "expectedRevision": 1 }`:
  `{ "revision": 1, "text": "…", "voiceAvailable": true }`.
- `POST /app/v1/game/boss/attempts/{id}/audio`, stejné tělo: `audio/mpeg`, nejvýše
  512 KiB. Syntetizuje pouze uložený text pro danou revizi, nikdy klientský text.
- `409`: neaktuální revize nebo text ještě není připravený; `503`: poskytovatel
  není dostupný nebo probíhá generování; `401/403/404`: běžné hranice přístupu.
  Odpovědi mají `Cache-Control: no-store`; řídí je běžný herní rate limit a CSRF.

## Ověření bez placených volání

```powershell
dotnet run --project backend-tests/BackendTests.csproj -c Release -- --mobile-game-boss
npm --prefix mobilni-aplikace run test:components -- src/features/game/GameBossNarration.spec.ts
```

Testy používají syntetické poskytovatele. Živý poslech vyžaduje samostatný souhlas
podle ElevenLabs onboardingu; bez něj se aplikace s klíčem nespouští ani řeč
nevytváří. Úspěšné offline testy nedokazují latenci či kvalitu skutečného hlasu,
funkci na fyzickém telefonu ani produkční nasazení.

Po výslovném souhlasu lze spustit `npm run baron:test`. Tento příkaz spotřebuje
kredity: přes oficiální SDK vytvoří jednu krátkou českou ukázku nálad hlasem
Barona na `eleven_v4` a uloží MP3 do systémového dočasného adresáře. Sám
negeneruje text přes GPT a nejde o záznam skutečně odehraného souboje. Nespouští
se automaticky při kontrole, sestavení ani startu serveru.
Placený požadavek má vypnuté automatické opakování a časový limit.

Rozhraní: [ElevenLabs TTS](https://elevenlabs.io/docs/api-reference/text-to-speech/convert),
[ověřování](https://elevenlabs.io/docs/api-reference/authentication).
Hlasové podání: [značky pro Eleven v4](https://elevenlabs.io/docs/help-center/product/core-capabilities/text-to-speech/how-do-audio-tags-work-with-eleven-v3-and-v4).
