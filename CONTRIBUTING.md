# Příspěvky do projektu

Projekt je rozpracovaný prototyp. Architekturu určuje
[implementační plán](docs/implementacni-plan.md), aktuální úkoly a blokace
vede [privátní koordinace](docs/development-coordination.md). Veřejná Issues jsou pro veřejné příspěvky. Začni
[návodem bez hardwaru](docs/public-development.md) a README dotčené komponenty.
Vlastní zdrojový kód, dokumentace a CAD zdroje projektu používají
[AGPL-3.0-only](LICENSE). Příspěvky do těchto částí poskytuj pod stejnou
licencí a pouze s oprávněním k jejich distribuci. U materiálů třetích stran
uveď původní licenci a zachovej jejich oznámení.

1. U běžné chyby otevři issue s komponentou, verzí a reprodukcí na syntetických
   datech. Zranitelnosti hlásí [SECURITY.md](SECURITY.md).
2. Zkontroluj privátní úkoly/claims a souběžná veřejná PR. Pracuj ve vlastní větvi nad aktuálním `development`; jeden úkol předávej jedním implementačním PR do `development`. Privátní odkazy a detaily zapisuj pouze do privátního úkolu.
   Nezahrnuj nesouvisející úpravy ani generované lokální výstupy.
3. Používej existující kontrakty a strukturu funkcí. Změnu kontraktu či
   architektury popiš s dopadem; neobnovuj Arduino profil pro kufr.
   UI používá [Arcane Workshop](design-system/README.md).
4. Použij připnuté SDK, `npm ci` a platformě odpovídající NuGet restore.
   Ověř dotčený projekt podle jeho README. C# backendové testy jsou spustitelný
   harness přes `dotnet run`, nikoli projekt pro `dotnet test`.
5. V PR uveď problém, změnu chování, skutečně provedené příkazy a výsledky.
   Rozlišuj fake/simulaci, hardware, iPhone a živý provider. Chybějící hardware
   nebo Apple identita nebrání příspěvku do nezávislého softwaru.
   Pro volitelnou souhrnnou lokální kontrolu můžeš spustit
   `python scripts/ci/preflight.py --profile quick --scope local`. Výchozí režim
   spouští npm testy a typecheck; backend sestaví a otestuje jen při změně jeho
   zdrojů nebo sdílených vstupů vůči `origin/development`. Podle změny přidej
   cílené ověření. Android/iOS buildy, balení a dlouhé integrace jsou v plném
   release CI nebo ve volitelném režimu `--profile full`. Návod
   [lokálních kontrol](docs/local-preflight.md) popisuje reporty, instalaci
   připnutých závislostí a zbývající ověření pro Linux, macOS a nativní buildy.
   Preflight ani jeho report nejsou podmínkou založení, aktualizace nebo sloučení
   PR. Povinné CI a review zůstávají zachované. Lokální chyby oprav před odesláním;
   nedostupné platformy v PR výslovně uveď.
6. U dokumentace zkontroluj místní odkazy, správnost příkazů a
   `git diff --check`. Po změně generátoru CAD použij skutečný postup
   [regenerace F2](mechanika/final/README.md), ne ruční úpravu exportů.

Nevkládej tokeny, PoP/párovací QR, klíče, certifikáty s privátním klíčem,
databáze, osobní fotografie či skutečné přepisy hostů do issues, PR ani testů.
Nové assety musí mít dohledatelný zdroj, autora a oprávnění k redistribuci;
zachovej jejich licenční oznámení.

Veřejný přispěvatel nepotřebuje přístup do produkce, podpisové identity ani
placené AI. Release, nahrání do desky, podpis a deployment spravuje vlastník.
Pravidla interního předání v [AGENTS.md](AGENTS.md) nejsou oprávněním externího
přispěvatele zapisovat do infrastruktury. Zelené CI nenahrazuje review nebo
fyzickou akceptaci. Platí [pravidla chování](CODE_OF_CONDUCT.md).

Dependabot otevírá týdenní skupinové PR pro Actions, npm a NuGet. Aktualizace
SDK, Xcode a PlatformIO se kontrolují ručně společně s jejich lock/pin soubory.
CODEOWNERS určuje vlastníka review. Vynucení ochrany větve závisí na dostupnosti
GitHub rulesets; skutečná omezení přístupu a tarifu jsou popsaná v CI/CD návodu.

Běžný tok je `pracovní větev → development → main`. PR do `development`
se slučují přes squash po kontrolách repozitáře a dotčených produktových testech. Vydání je samostatný PR
`development → main`, který spouští všechny běžné buildy a slučuje se přes
merge commit. Podrobnosti, ochrany větví a první aktivaci popisuje
[Git workflow](docs/git-workflow.md). Povýšení do `main` vyžaduje zadání vydání;
běžný vývojový úkol končí předáním do `development`.
