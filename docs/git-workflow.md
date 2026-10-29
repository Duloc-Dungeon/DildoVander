# Vývoj přes development

`pracovní větev → development → main`

`development` shromažďuje změny pro další vydání. `main` představuje vydávaný
kód. Soukromý deployment repozitář má vlastní pravidla a tento postup jeho
větve ani produkční oprávnění nemění.

## Běžná změna

1. Načti `origin/development` a založ z něj vlastní pracovní větev v task
   worktree. Existující task worktree znovu použij; zachovej ostatní změny.
2. Proveď změnu, lokálně ověř dotčenou funkci a otevři PR do `development`.
3. Po review a úspěšném `Development required` použij squash merge.

Development CI běží pro všechny PR do `development`, včetně dokumentace.
Ověřuje úniky tajných údajů přes Gitleaks, syntaxi workflow přes actionlint,
testy CI skriptů, kontrakty modelových workflow, whitespace a integritu design
systému. Nespouští kompilaci backendu, webu, firmware, Androidu ani iOS;
nevytváří distribuční artefakty a nepodepisuje aplikace. Neověřuje proto
kompilovatelnost aplikace; za lokální cílené ověření odpovídá autor změny.
Chyba nebo přeskočení kterékoli rychlé kontroly blokuje agregovanou kontrolu.

Ochrana `development` vyžaduje PR, vyřešené review diskuse a tuto rychlou
kontrolu. Zakazuje force push a smazání větve. Nevyžaduje aktualizaci větve
po každém souběžném merge, aby zbytečně neopakovala CI. Konflikty musí autor
vyřešit před sloučením. Dependabot otevírá PR do `development`; generování
locků a nativních závislostí zachovává stávající oddělené bezpečnostní hranice.

## Vydání

1. Vydání zahaj samostatným zadáním. Otevři PR z `development` do `main`.
2. Při konfliktu nebo zastaralém základu sluč aktuální `main` do
   `development`, vyřeš konflikty a nech kontroly doběhnout znovu.
3. `CI required` pro tento PR vyžaduje všechny existující běžné projekty:
   repository, backend, web, iOS, Android, offline AI evaluace a site.
   Platí to i pro samotnou dokumentaci. Placené evaluace a CAD generování
   zůstávají samostatné explicitní operace.

   Fyzický firmware, CAD a softwarový simulátor jsou pozastavené a automatické PR/release buildy je
   nevybírají. `project=all` je nezahrnuje. Ruční `project=firmware` a samostatné
   ruční CAD workflow zůstávají dostupné. Backend simulátor standardně
   nekompiluje ani nepřibaluje; ruční režim popisuje lokální preflight.
4. Po úspěšném CI a review použij **Create a merge commit**. Squash tohoto PR
   by ztratil společnou historii a komplikoval další vydání.
5. `development` zachovej. Následující pracovní větev znovu založ z něj.

Push na `main` nadále vytváří release artefakty a podpisy. Stávající oddělená
soukromá orchestrace může úspěšný aktuální release automaticky nasadit včetně
TestFlight. Proto slučování běžného vývojového PR do `development` samo
nespouští vydání. `main` zůstává výchozí větví repozitáře kvůli důvěryhodným
řídicím workflow; při zakládání PR vždy výslovně zvol cíl `development`.
Starší PR přímo do `main` přesměruj do `development`; jejich dosavadní CI
zůstává během přechodu dostupné. Ruční recovery buildy na `main` zůstávají.

Úplné PR CI ukládá doklad o provedených kontrolách. Pokud release nezávisle
prokáže shodu tohoto dokladu s výsledným merge commitem, na `main` už znovu
nespouští stejné testovací sady. Znovu sestaví distribuční balíčky, ověří novou
binárku a zachová kontrolu historie na tajné údaje. Chybějící, neúplný nebo
zastaralý doklad znamená plné testy. Podrobnosti jsou v
[ověření vydání](release-validation.md). Volitelný
[rychlý lokální preflight](local-preflight.md) nabízí backendové regresní skupiny,
npm testy, typecheck a krátké kontroly repozitáře. Nativní buildy, balení
a dlouhé HTTP/browserové integrace zůstávají v plném PR CI nebo ve volitelném
lokálním režimu `--profile full`. Preflight ani jeho report nejsou podmínkou
založení, aktualizace nebo sloučení PR; povinné CI a review zůstávají zachované.

## První aktivace

Samotné soubory v pracovní větvi nemění serverová pravidla GitHubu.
Správce při aktivaci:

1. Založí trvalou větev `development` z aktuálního `main` a doručí do ní
   tuto změnu. Vytvoření nebo push `development` nespouští release workflow.
2. Prohlédne plán přes `bash scripts/ci/apply-repository-settings.sh`.
   Po schválení aplikuje `--apply`: povolí merge commity, nastaví hlavní
   pravidlo na merge commit a přidá ochranu `development`. Žádné pravidlo
   nepřidává bypass. Ochrana smazání zachovává `development` i při zapnutém
   automatickém mazání sloučených pracovních větví.
3. Při schváleném vydání otevře první PR `development → main`, dokončí plné
   kontroly a sloučí přes merge commit. Řídicí workflow načítaná z výchozího
   `main`, včetně konfigurace Dependabotu a publikace jeho artefaktů, přejdou
   na nová pravidla až tímto krokem. Aktivace nesmí obejít ochrany větví.

Pokud se mají vrátit stará pravidla, nejprve zastav nové změny a prohlédni
nesloučené PR. Vrať workflow a serverová pravidla společně, bez force push
nebo mazání `development`. Před každým přesunem do `main` zohledni následné
automatické vydání a nasazení.
