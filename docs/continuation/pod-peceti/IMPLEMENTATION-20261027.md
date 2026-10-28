# Pokračování: C1 a serverové základy časování/příkazů

Tento záznam popisuje novou práci v jednom Codex chatu nad čistým checkpointem
`71b0349bac4f5b8fb9d195252339474735c6d7b6`. Historické Hermes reporty nejsou
výsledky zde provedených testů. Žádný board, heartbeat ani další chat nebyl založen.

## C1: uzavřený lifetime/privacy rework

- Každá produkční case operace rezervuje case → identity → postavy; již držené
  admission rezervace se znovu neotevírají. Kontrolují se vlastníci setupu,
  historických akcí, snapshotů a rosteru, včetně nového přicházejícího aktéra.
- Durable profile/account fence má přednost před peer read, capture, freeze,
  kontextem, replay i opakovaným Create. Cleanup probíhá až mimo identity/character
  guard. Tombstone/saga zůstává pending při chybě a nový worker ji může dokončit.
- `ResolveFrozenCharacter` je server-only a vyžaduje live admission skutečného
  člena. Vrací vybraný profil/enrichment, portrait metadata a dostupný blob;
  nepřepíná na current revizi a negeneruje nový obraz.
- Legacy unfrozen ready bez snapshotu dostane trvalou invalidaci a požadavek
  nového výběru/souhlasu. Frozen výběr se nepřepisuje.
- Erase je atomický terminal fallback celého dotčeného případu. Zdůvodnění:
  world/resolution JSON propojuje osobní identifikátory a nelze bezpečně zachovat
  část kauzální historie po odstranění účastníka. Minimální redigovaný terminal
  marker brání resurrection; nedotčené případy zůstávají beze změny. DELETE
  výjimka se omezuje na terminal case, nikoli vypnutí triggerů během cleanupu.
- Cleanup funguje i bez OperationJournal a po vypnutí SealedCase feature,
  pokud existuje dříve vytvořená case databáze.

Hlavní zdroje jsou `backend/Features/SealedCase/SealedCasePrivacy.cs`,
`SealedCaseFrozenContent.cs`, `SealedCaseErasure.cs`, `SealedCaseLegacySelections.cs`
a odpovídající persistence/admission/deletion návaznosti.

Nezávislý read-only deep reviewer uzavřel rework bez zbývajících nálezů.
Review fingerprint 17 zdrojových/testovacích souborů:
`d1e0838e4281317cdca273ff297047584b85b298bdd6aece6f0a8e2e3e14affa`.
Přesné bytes této revize jsou zachované v lokálním reviewed-source archivu.

## C2/C3 a D1/D2: ověřená backendová hranice

`SealedCaseTiming.cs` ukládá serverový začátek, původní variantu/N a pauzy.
Rozhodování, klid, úvod a finále vycházejí z existujících pravidel. Pause/resume
je creator-only, atomické s eventem/revizí a idempotentní. Polling nemění deadline.

`SealedCasePlans.cs` přijímá pouze výslovně potvrzený návrh; actor, ID a přijatá
revize pocházejí ze serveru. Ukládá se poslední potvrzená verze každého hráče;
starší verze zůstávají v journalu. Identický retry vrací původní receipt i po
uzávěrce, ale nevytváří novou akci. Nepotvrzený návrh se nezapíše.

`ResolveConfirmedChapter` čte kanonické plány ve stejné transakci jako
vyhodnocení a ukládá náhodu/výsledky přes existující world resolver. Vynechané
kapitoly nelze přeskočit; opožděný worker je může uzavřít postupně. Extrémní tah
se spotřebuje až při dosaženém střetu, přesně jednou při atomickém vyhodnocení.
Timed game odmítá starou raw action/resolver cestu.

Toto je backendový základ. Není tím doložený scénářový koordinátor, skutečný
reconnect mobilního UI ani kompletní acceptance původních C2–D2 karet.
Budoucí transport musí actor/group svázat s live serverovou identitou;
world provisioning musí používat autoritativní frozen roster. Soukromé interní
DTO se nesmějí automaticky použít jako klientské kontrakty.

Nezávislé focused review devíticestného delta nemělo otevřené vady. Po něm byly
přidány pouze hraniční testy a spuštění existující account-deletion skupiny v CI.
Produkční chování zůstalo shodné s reviewed source; závěrečná úprava doplnila pouze závorky control flow podle místních pravidel a znovu prošla buildem.

## Nová validace

SDK: .NET 10.0.303; framework-dependent Release na Windows.

| Příkaz / hranice | Výsledek |
| --- | --- |
| `dotnet restore backend-tests/BackendTests.csproj --locked-mode` | PASS |
| `dotnet build backend-tests/BackendTests.csproj -c Release --no-restore` | PASS, 0 warnings/errors |
| `dotnet backend-tests/bin/Release/net10.0/BackendTests.dll --sealed-case-runtime` | PASS po integraci timing/plans a hraničních testech |
| C1: samostatné `--sealed-case-c1`, `--account-deletion`, `--sealed-case-persistence` po review opravách | PASS |
| C1: `--sealed-case-http`, `--sealed-case-context-persistence`, `--customer-profiles`, `--character-portraits` | PASS; runtime znovu zahrnuje HTTPS a context/persistence |
| UI source diff proti cleanup `c91166ce...` | Prázdný pro `mobilni-aplikace/src` a `design-system` |
| `git diff --check` | PASS |
| Hosted CI / Linux self-contained / browser / native iOS / live AI / lidský pilot | NOT RUN |

Regrese zahrnují syntetický image/enrichment přes edit/freeze/reopen, queued/
failed/absent, peer fences, newcomer fence, disabled cleanup, skutečně naplněný
world/resolution erase, rollback a pending ack, restart před ack, controlled
writer-wait × edit/delete/revoke, readiness 8/30, legacy upgrade, timer hranice,
quiet/finale pause, souběžné pause/confirm, writer-wait × deadline, canonical
resolution/replay a jednorázový debit.

První neúspěšné lokální pokusy odhalily fixture consent/session nastavení a
Windows SQLite pooling při testovacím cleanupu; byly opraveny. Nejsou započtené
jako průchody. Workflow test flags jsou ověřené proti Program.cs; YAML parser
v místním Pythonu nebyl dostupný, hosted workflow nebyl spuštěn.

Raw logy, příkazy/rc, input/output manifesty a receipt jsou lokálně v ignorovaných
`backend-tests/bin/pod-peceti-c1-evidence/` a `pod-peceti-timing-evidence/`.
Nejsou to provozní databáze ani produkční AI evidence. Aktuální fingerprint všech
22 změněných backend/test/workflow zdrojů:
`48657d69d360eb5959be7536325c09f38786e04063898d4fa6b97ef417a7324a`.

## Zbývá

Dokončit scénářový adaptér/koordinátor a zbývající mechaniky, následně oprávněné
AI projekce a fallbacky, herní tok v cleanup UI, hlasování/rekonstrukci, provozní
hranice a E2E. Veřejný start/actions stále správně vracejí
`sealed_case_scenario_unavailable`. Není doložená hratelná mobilní partie ani pilot.

Změny zůstávají lokální a unstaged. Žádný commit, push, merge, deploy, externí
zpráva ani produkční AI nebyly provedeny. Nové commit/delivery kroky vyžadují
příslušné oprávnění a platný review postup.
