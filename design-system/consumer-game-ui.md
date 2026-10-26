# Consumer game UI

## Aktuální směr

Závazný [profil aplikace](../PRODUCT.md) a [DESIGN.md](../DESIGN.md) určují
jazyk, humor, zaměření a vzhled. **The Responsive Table**, Preview 2,
26. října 2026, je aktuální předloha pro mobilní zákaznický zážitek.
[Uložená předloha](reference/responsive-table.png) a runtime obrazové zdroje
jsou dostupné v každém checkoutu; osobní složka Downloads není závislost.

Zelený stůl, mosazné akce, čitelná bezpatková typografie, jednoduchá navigace
postava/hra/památky/účet a hmatové herní žetony. Vlastník má přímý vstup do
večera a pozvánek. Ovládání kufru je mimo běžný mobilní zážitek.

## Mapa implementace

| Oblast | Vlastník |
| --- | --- |
| Barvy, typografie, společný rytmus, pohyb | `mobilni-aplikace/src/application/consumer-brand.css` |
| Navigace a domov | `src/application/App.vue`, `HomeView.vue` |
| Hostitel | `EveningSetupLinks.vue`, `features/launch/LaunchManagementView.vue` |
| Postava a pomoc se zadáním pro AI | `features/customer/CustomerView.vue`, `CharacterBriefAssistant.vue` |
| Stůl, skóre, žetony a výsledky | `features/game/PlayView.vue`, `game.css` |
| Runtime ilustrace stolu a žetonů | `src/assets/brand/responsive-table/` s manifestem původu |
| Pozvánka a čekárna | `features/identity/SignInView.vue`, `features/launch/LaunchView.vue` |
| Společná scéna a opona | `components/scene/`, `features/launch/PortraitCurtain.vue` |

Cesty začínající `src/` jsou relativní k `mobilni-aplikace/`.
Pravidla hry, oprávnění, AI souhlasy a serverové snapshoty vlastní existující
feature moduly. Prototyp je vizuální reference; jeho ukázkový soupeř, skóre,
portréty a osmikolový místní scénář nejsou nové produkční pravidlo.

## Pozvánka a opona

Dřívější Midnight Table dodal používané pozvánkové a divadelní assety.
Zachovávají se účelové vrstvy `SceneShell`, `PaperSurface`, `InvitationSeal`,
`CharacterCountdown` a `PortraitCurtain`. Osmisekundový viditelný úvod neodkládá
zápis; hotový obraz, chyba a serverem povolený vstup mají přednost. Zmeškané
otevření téže revize se při návratu přehraje, dokončené se neopakuje.
Rám a volán stojí, otevírají se dvě samostatná křídla, průhled je přesně 4:5.
Žádná výrobní procenta ani vtipy v opakované live oblasti.

[Starý šestistavový board](reference/midnight-table-board.png),
[datovaná evidence](evidence/midnight-table/README.md) a
[zdrojové assety](assets/midnight/README.md) zůstávají historickým záznamem
funkčních stavů a původu, nikoli konkurenčním pravidlem pro současný vzhled.
Původní patková typografie, hnědofialové povrchy a anglický přepínač se neobnovují.

## Ověření

Po buildu používej aktuální skutečné komponenty a izolované fixture:
`npm run test:browser`, `test:owner:browser`, `test:game:browser`,
`test:invitation:browser` a příslušné komponentové testy podle změny.
Ověř 320/390 px, desktop, 200% text, klávesnici, safe areas, oba kompatibilní
identifikátory témat, chyby a omezený pohyb. Herní výsledek ani možnost akce
nesmějí čekat na dokončení dekorativní animace.

Výsledky browseru dokazují pouze konkrétní software a fixture. Živá AI,
produkční účty, skutečný přístupový klíč a nativní zařízení potřebují vlastní
důkaz. Nová reference sama neznamená provedenou akceptaci.
