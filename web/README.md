# Veřejná aplikace · Vapour Chrome

Hostovaná aplikace vzniká z `mobilni-aplikace` a používá stejné Vue obrazovky
a router jako iOS a Android: přihlášení, Kufrovu dílnu, hry, postavu, památky,
účet a oprávněnou správu. Staré ovládání fyzického Kufru, Provoz/Režie,
3D Dílna a původní Archiv jsou odstraněné. Jejich adresy slouží pouze jako
kompatibilní vstupy do současné aplikace. `/ios/` a instalační manifesty
zůstávají samostatnou distribuční výjimkou. Změna zdrojů neznamená nasazení.

Detaily `/keepsake/` a `/archive/duel` stále používají současné Památky.
Oprávnění vlastníka a omezené guest granty zůstávají samostatné.
Proxy generuj z `deploy/keepsake_proxy.py`, bez obecného API catch-all.

`build-site.py --web-dist <dist> --destination <nový-adresář>` balí výhradně
hotovou aplikaci. Neimportuje CAD Python, negeneruje geometrii, neověřuje
výrobní ZIP a nekopíruje staré Companion, simulátor, guest ani viewer balíčky.
`site-manifest.json` schema 2 odděluje revizi aplikace, příznak rozpracovaného
zdroje, verzi API a hashe souborů od nezávislé revize ModelBundle.
Výstup obsahuje jeden HTML vstup a kompilované prostředky. Publikace odmítá
rozpracovaný zdroj, neplatný inventář a soukromé soubory.

Geometrii, ModelBundle a výrobní výstupy stále vlastní samostatná CAD pipeline.
Aplikace již nenabízí 3D prohlížeč ani offline viewer; změny aplikace
necertifikují výrobní výstupy znovu.

`/keepsake/` zachovává návštěvnické odkazy DV10; `/keepsake/evening/` vede přes
router do současných Památek. Owner URL `/keepsake/?k=<locator>` sama neuděluje
přístup. Fragmenty `#c=`, `#r=` a `#s=` zachytí kořenový inline skript před
načtením modulů a odstraní je z adresy; výměna vyžaduje výslovnou akci hosta.
Soukromé guest API používá vlastní cookie, CSRF a idempotentní mutace.
Veřejné GET přehledy používají `credentials: omit`. Fotografie se odesílá až
po souhlasu; GET, polling ani příchod na routu nespouští generování.
Přihlášený účet nenahrazuje guest capability ani firmware credential.

Nginx používá přesný seznam SPA stránek. API chyby, websockety, auth callbacky,
manifesty, downloady a chybějící prostředky nepřepisuje na HTML. Claim odkazy
nepřesměrovává. HTML a privátní odpovědi mají `no-store`; privátní guest stránky
navíc `noindex` a `no-referrer`. Fingerprintované `/assets/` a neměnné modelové
balíčky mají oddělenou cache. Aktivační reference modelu zůstává `no-store`.

CI projekt `site` sestavuje aplikaci bez CAD toolchainu.
CAD nemá povinnou ani automatickou CI gate. Rychlý lokální build/publish a ruční
generation-only fallback popisuje [CAD workflow](../mechanika/final/CAD-WORKFLOW.md).
Neznámý mechanický soubor zůstává konzervativně výrobním vstupem pro výslovnou
lokální výrobní validaci. Ruční `project=site` nespouští CAD;
`project=all` vybere obě nezávislé gate. Přímý ruční build `main` bez PR vstupu
zachovává kompatibilní název artefaktu `arcane-site`. Public workflow neobsahuje
produkční credentials ani produkční publisher.

Lokální kontroly:

```powershell
python scripts/ci/ui_boundary.py
python -m unittest discover -s web -p 'test_*.py'
python -m unittest discover -s web/deploy -p 'test_*.py'
npm run test:browser --prefix mobilni-aplikace
```

Samostatné staré guest stránky a projektový homepage již nemají build vstup.
Také recovery aplikace používá stejný kompilovaný Vue výstup a příkaz výše;
privátní guest routy nadále dostávají HTTP `noindex`, `no-store` a `no-referrer`.
Schema-1 publisher/readback zůstává jen pro již vydané neměnné archivy, nikoli
pro sestavení druhého UI. Browser testy samy neprokazují fyzický iPhone,
kvalitu OpenAI ani výrobní přijetí.

[Postup přípravy release, persistence a rollback](deploy/APPLICATION-RELEASES.md)
popisuje oddělené aplikace/modely, atomickou lokální aktivaci a hranice důkazů.
[CI/CD](../docs/ci-cd.md) popisuje oddělení veřejných kontrol a privátního
nasazování. Produkční nginx, oprávnění a health je nutné ověřit při samostatně
autorizovaném rolloutu.
