# Pod pečetí — rozpracovaný checkpoint 2026-10-27

## Navazující změna: AI hráči v lobby

Implementováno přidání AI míst pouze pro ownera/zakladatele před uzavřením lobby,
bez vytváření účtů. Owner může zůstat nesoutěžícím pozorovatelem bez zákaznické
role. AI má vlastní omezený pohled, trvalé rozhodovací lease/retry a společné
privacy/erase ochrany. Výchozí konfigurace je vypnutá; produkční AI ani deployment
se touto změnou nespouštějí. Nové offline HTTP/browser regrese jsou zelené.
Detaily a provozní nastavení: [AI-PLAYERS.md](AI-PLAYERS.md).


## Stav pokračování — 28. 10. 2026

Hotové jsou hratelné serverové a klientské celky C1–M1: obě varianty, potvrzované
plány, scénář a všechny podporované úmysly, soukromé projekce/zprávy, finále,
rekonstrukce a výslovný export. Runtime používá faktické vyprávění; šest
serverových AI rolí má omezený read-only adaptér a fallback, bez zapnutého
produkčního providera. Cleanup vzhled zůstává zachovaný.

Nově ověřeno: souhrnný backend runtime včetně privacy restart/erase/ack, HTTPS
partie 8/30 klientů se souběžným finále, explicitní krytí/dohled/krádež/ústup,
frontend build a browser 320/390 px s lost-ack/reload. Nezávislé review nemá otevřené
critical/high/medium. Cílený nový AI boundary test prošel proti novému buildu.
Podrobné příkazy, evidence, runbook a pilotní postup jsou v
[ACCEPTANCE-20261028.md](ACCEPTANCE-20261028.md).

Z autorizovaného maxima 10 AI partií jsou započítány čtyři: první selhala při
souběžném finále, po opravě prošel První případ i full s osmi AI hráči. Čtvrtý
pokus potvrdil nový bounded transport a dokončil První případ. Celkem 3 PASS,
1 FAILED; žádná další placená partie není naplánována. Ledger zůstává uložený.
Nově prošly také šestice AI boundary rolí, account deletion saga/HTTPS a browser
s klávesnicí, 200% textem a reduced-motion.

Zbývá doložit lidský pilot N5 a nativní iOS; obojí NOT RUN, bez tvrzení o produkční
připravenosti. Bez commitu, push, merge, deploy, Hermes nebo heartbeat.

Checkout HEAD zůstává `71b0349bac4f5b8fb9d195252339474735c6d7b6`. Startup vyžaduje
vlastněnou větev `codex/parent-282fee468f44bdac`; ta nyní ukazuje na tentýž
checkpoint. Reference `codex/pod-peceti-project` je zachovaná. Původní plánovaný
base má samostatnou recovery referenci. Kanonické zdrojové soubory ani původní
handoff checkout se neměnily. Níže uvedený stav z 27. 10. je historický.


Hermes byl na výslovnou žádost vlastníka zastaven kvůli pomalému a nákladnému workflow. Pokračování má vést jeden Codex chat. Toto je archivní WIP checkpoint, nikoli schválené dokončení nebo release.

## Aktuální pokračování v Codexu

- Převzatý čistý checkpoint: `71b0349bac4f5b8fb9d195252339474735c6d7b6`, větev `codex/pod-peceti-project`.
- Jediný zapisující checkout tohoto chatu: `C:/Development/DildoVander/.worktrees/app/parent-282fee468f44bdac`. Přesná historie byla lokálně načtena z předaného checkoutu v Dokumentech; ten ani VPS se souběžně nemění. Kořen byl určen nadřazeným session pokynem.
- C1: rework R1/run 43 uzavřen nezávislým source review a cílenými regresními testy. Doplněny rezervace case → identity → postavy pro všechny vlastníky, server-only frozen resolver, cílená terminal erase výjimka, cleanup i při vypnutém feature a durable legacy invalidace/reselect.
- Nově ověřeno: .NET 10.0.303 Release build bez varování; `--sealed-case-c1` PASS (syntetický obraz/enrichment přes edit/freeze/reopen, denial, restart/failure/before-ack a řízené souběhy). Navazující HTTPS/context/profile/portrait skupiny PASS; po review opravách znovu PASS C1/account-deletion/persistence. Review bez otevřených nálezů, source fingerprint `d1e0838e4281317cdca273ff297047584b85b298bdd6aece6f0a8e2e3e14affa`.
- Nová lokální evidence: `backend-tests/bin/pod-peceti-c1-evidence/` (ignorované logy, příkazy/rc, vstupní hashový manifest). Předchozí jednotlivé neúspěšné pokusy jsou v historii tohoto chatu; nejsou vydávány za průchody.
- Nově implementované a ověřené serverové základy: C2/C3 durable časování/pauza a D1/D2 výslovné potvrzení/revize/kanonické vyhodnocení příkazů. Release a agregovaná `--sealed-case-runtime` PASS; focused review bez otevřených vad. Scénářový koordinátor, skutečný UI reconnect a celé navazující karty tím nejsou dokončené.
- Podrobnosti a přesná omezení: [IMPLEMENTATION-20261027.md](IMPLEMENTATION-20261027.md). Veřejný start zůstává fail-closed do scénářové integrace.
- Žádný commit, push, merge, deploy, produkční AI ani obnovení Hermes/heartbeat. UI soubory se nemění. Nativní iOS a lidský pilot nejsou ověřeny.

## Repozitář a zachované chování

- Branch: `codex/pod-peceti-project`; výchozí cleanup: `c91166ce9adab0eea4ced9e40e4a731548f05b15`.
- VPS checkout: `/workspace/worktrees/dildovander/pod-peceti`.
- Zachovat UI cleanup, český produktový hlas a stávající večery, pozvánky, identity, postavy a portréty. Předlohy v podkladech neopravňují k redesignu.
- Podklady: externí předávací balíček vedle tohoto repozitáře, adresáře `podklady`, `cards`, `evidence`, export boardu. Originál ZIP je přiložen samostatně.
- V repozitáři jsou souhrn a pokračovací prompt; runtime databáze, autentizace, provozní logy a zdrojové podklady se nepublikují do produktového Gitu.

## Historický stav při předání (před tímto chatem)

Z původních 32 karet jsou dokončeny A1 (inventura), A2 (verzování podkladů), B1 (autoritativní stav/event log) a B2 (projekce/autorizace). Celkem board obsahuje 40 karet: další dekompozice přidala 8 dílčích úloh, z toho 4 hotové. Údaj 8 hotových tedy neznamená 8 z původních 32.

C1 zůstává nedokončená. Poslední aktivní karta `t_ca54728a` byla v review; run 44 byl při zastavení reclaimed. Předchozí run 43 výslovně předal jen částečný rework. Všechny ostatní otevřené karty byly zaparkovány jako scheduled; board metadata archived=true zamezují automatickému review dispatchi. Nespouštět Hermes ani neodparkovávat karty.

## Historické R1 nálezy — opravené výše popsaným reworkem

Nezávislé C1 review R1 (run 42) reprodukovalo závažné nedostatky:

1. Retence smazaného profilu v case snapshotu po DeleteAsync/reconcile/reopen. Dokončit durable privacy fence pro všechny vlastníky, peer deny, terminaci, cílený restartovatelný erase a ack až po cleanup bez opačného lock orderu.
2. Chybí autorizovaný server-only frozen content resolver.
3. Chybí synthetic-portrait/edit/reopen a deterministické capture/delete/freeze/replay/peer-read/restart/no-resurrection regrese.
4. Legacy-ready count/freeze nekonzistence: explicitní upgrade invalidace/reselect.
5. Evidence musí odpovídat aktuálním zdrojům; historický HTTP hash neseděl.

Run 43 přidal jen fence-aware eligibility/read kontroly, case-scoped erasure při reconcile/account deletion a regresi atomického scrub/replay deny. Netvrdit, že tím byly všechny výše uvedené nálezy vyřešeny. Plné review je v `evidence/t_ca54728a/review-round1/REVIEW.md`; poslední rozpracované review je také přibaleno, pokud existuje.

Původní B1/B2/C1 záměrně odmítá start a akce s `sealed_case_scenario_unavailable`. Existuje interní allocator hráčů 8–30 se stabilními seats a oddělením secret team od kit, ale hra/scénář není hotový. Lobby má jeden LaunchGroup; dle zaznamenaného upřesnění se může připojit kterýkoli aktivní člen do kapacity, bez host whitelistu.

## Testy a meze důkazů

Historické worker reporty a příkazy jsou v balíčku. C1 run 43 uvádí PASS Release build, persistence a HTTPS skupiny a diff check; run 42 měl osm harness skupin PASS, ale vlastní acceptance probe FAIL. Historické průchody nepovažovat za finální acceptance. Při checkpointu znovu kontrolujeme pouze integritu snapshotu a `git diff --check`; nespouštíme další vývojovou/testovací smyčku po požadavku stop.

Při pokračování ověř skutečné přepínače v backend-tests/Program.cs. BackendTests je executable harness, nikoli dotnet test. SDK a runtime respektuj dle global.json/AGENTS.md; pro Linux lze použít stávající sandbox. Frontend build kontroluje contracts generated; neměnit generated ručně. Nespouštět produkční AI, deployment, push ani merge bez příslušného oprávnění.

## Původní pokračovací pořadí a zbývající acceptance

C1 rework je nyní uzavřený podle nového záznamu nahoře. Dokončit scénářové/klientské zapojení nových serverových základů a pokračovat původním backlogem C2, C3, D1/D2, E1/E2, F1/F2, G1/G2, H1/H2, I1/I2, J1/J2, K1/K2, L1/L2, M1/M2, N1–N5 podle skutečných závislostí a podkladů. Člověkem provedený pilot ani fyzické iOS ověření nebyly doloženy.
