# Pod pečetí — rozpracovaný checkpoint 2026-10-27

Hermes byl na výslovnou žádost vlastníka zastaven kvůli pomalému a nákladnému workflow. Pokračování má vést jeden Codex chat. Toto je archivní WIP checkpoint, nikoli schválené dokončení nebo release.

## Repozitář a zachované chování

- Branch: `codex/pod-peceti-project`; výchozí cleanup: `c91166ce9adab0eea4ced9e40e4a731548f05b15`.
- VPS checkout: `/workspace/worktrees/dildovander/pod-peceti`.
- Zachovat UI cleanup, český produktový hlas a stávající večery, pozvánky, identity, postavy a portréty. Předlohy v podkladech neopravňují k redesignu.
- Podklady: externí předávací balíček vedle tohoto repozitáře, adresáře `podklady`, `cards`, `evidence`, export boardu. Originál ZIP je přiložen samostatně.
- V repozitáři jsou souhrn a pokračovací prompt; runtime databáze, autentizace, provozní logy a zdrojové podklady se nepublikují do produktového Gitu.

## Skutečný postup

Z původních 32 karet jsou dokončeny A1 (inventura), A2 (verzování podkladů), B1 (autoritativní stav/event log) a B2 (projekce/autorizace). Celkem board obsahuje 40 karet: další dekompozice přidala 8 dílčích úloh, z toho 4 hotové. Údaj 8 hotových tedy neznamená 8 z původních 32.

C1 zůstává nedokončená. Poslední aktivní karta `t_ca54728a` byla v review; run 44 byl při zastavení reclaimed. Předchozí run 43 výslovně předal jen částečný rework. Všechny ostatní otevřené karty byly zaparkovány jako scheduled; board metadata archived=true zamezují automatickému review dispatchi. Nespouštět Hermes ani neodparkovávat karty.

## Otevřené nálezy — první priorita

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

## Následující práce

Nejprve uzavřít C1 a výše uvedené nálezy s cílenými regresními testy. Pak pokračovat původním backlogem C2, C3, D1/D2, E1/E2, F1/F2, G1/G2, H1/H2, I1/I2, J1/J2, K1/K2, L1/L2, M1/M2, N1–N5 podle skutečných závislostí a podkladů. Člověkem provedený pilot ani fyzické iOS ověření nebyly doloženy.
