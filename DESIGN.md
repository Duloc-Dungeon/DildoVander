---
name: "Wand Night Stand — design v2"
description: "Tmavé společné jeviště, výrazná postava a čitelná hlavní akce."
colors:
  mint: "#b8f3d4"
  on-mint: "#102419"
  amber: "#ffc662"
  on-amber: "#211607"
  violet: "#c8b9fc"
  on-violet: "#241a37"
  peach: "#f8b5a5"
  on-peach: "#301814"
  blue: "#b6ddf5"
  bg: "#080909"
  surface: "#191b1b"
  surface-raised: "#222626"
  line: "#343939"
  text: "#f5f5f0"
  muted: "#b2b9b6"
  input-bg: "#202523"
  input-border: "#38403b"
  placeholder: "#9ba69e"
  navigation-bg: "#111413"
  navigation-muted: "#9ca7a0"
typography:
  headline:
    fontFamily: "'Manrope Variable', sans-serif"
    fontSize: "clamp(1.9rem, 8.5vw, 2.5rem)"
    fontWeight: 760
    lineHeight: 1.12
    letterSpacing: "-0.045em"
  title:
    fontFamily: "'Manrope Variable', sans-serif"
    fontSize: "1.35rem"
    fontWeight: 760
    lineHeight: 1.18
    letterSpacing: "-0.025em"
  body:
    fontFamily: "'Manrope Variable', sans-serif"
    fontSize: "15px"
    fontWeight: 400
    lineHeight: 1.65
  label:
    fontFamily: "'Manrope Variable', sans-serif"
    fontSize: "1rem"
    fontWeight: 800
    lineHeight: 1.45
    letterSpacing: "-0.01em"
rounded:
  field: "14px"
  button: "16px"
  card: "22px"
  sheet: "24px"
  hero: "28px"
spacing:
  "8": "8px"
  "12": "12px"
  "14": "14px"
  "16": "16px"
  "20": "20px"
  "24": "24px"
  "32": "32px"
components:
  button-primary:
    backgroundColor: "{colors.mint}"
    textColor: "{colors.on-mint}"
    typography: "{typography.label}"
    rounded: "{rounded.button}"
    padding: "8px 14px"
  button-secondary:
    backgroundColor: "{colors.surface-raised}"
    textColor: "{colors.text}"
    typography: "{typography.label}"
    rounded: "{rounded.button}"
    padding: "8px 14px"
  button-danger:
    backgroundColor: "{colors.peach}"
    textColor: "{colors.on-peach}"
    typography: "{typography.label}"
    rounded: "{rounded.button}"
    padding: "8px 14px"
  input-text:
    backgroundColor: "{colors.input-bg}"
    textColor: "{colors.text}"
    rounded: "{rounded.field}"
    padding: "8px 12px"
  card:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.text}"
    rounded: "{rounded.card}"
    padding: "20px"
  navigation:
    backgroundColor: "{colors.navigation-bg}"
    textColor: "{colors.navigation-muted}"
    padding: "10px 12px"
---

# Wand Night Stand — schválený design v2

## Overview

**Creative North Star: "Wand Night Stand — design v2"**

Téměř černé pozadí, místní Manrope s českou diakritikou, velké samostatné
objekty a ploché jantarové, mintové a fialové karty. Jedna dominantní scéna,
čitelná hlavní akce; žádný trvalý ambient ani povinné dekorativní částice.
Toto je záznam schváleného systému a současné implementace, nikoli nový návrh.

**Key Characteristics:**

- Jedna výrazná postava nebo objekt místo dekorativní výplně.
- Plošné barevné plochy a tonální oddělení na tmavém podkladu.
- Stejná rodina písma pro nadpisy, text a ovládání.
- Výrazná hlavní akce, klidné vedlejší ovládání a viditelný fokus.

Zdroj návrhu: `WNS-kompletni-balik-v2.zip`, edice 2.0, 26. 10. 2026.
SHA-256 archivu: `cc4a584aead44719589b54740670b3745095c15d99132b0aead9fd047abc5672`.
Inventura návrhu vychází z `8c202e50ab4a86bf702203a49b79d4943c0e3fc1`.
Implementace začala na `c5a7ffb54121f3aa974cf0fa5b1e18addc37b19f` větve cleanup;
její odstraněné podklady se neobnovují hromadně.

Manrope OFL a Phosphor MIT se distribuují s výsledným webovým balíkem.

- Převzaté tokeny: `mobilni-aplikace/src/assets/wns-v2/design/tokens.json` a CSS.
- Aplikační adaptér: `mobilni-aplikace/src/application/wns-v2.css`.
- Původní manifest: `mobilni-aplikace/src/assets/wns-v2/source-manifest.json`.
  Jde o inventář zdrojového archivu, nikoli seznam všeho distribuovaného klientem.
- Demo Matylda a audio sampler se do nové aplikační sady nekopírují.
  Žádný skutečný soukromý portrét se nestává veřejným assetem.
- Existující `--aw-*` názvy zůstávají kompatibilní. Obnovené runtime soubory
  `design-system/` a pět JS modulů `mechanika/final/` jsou stále importované
  aplikací/offline viewerem; nejsou obnovením vývoje fyzického kufru.

## Colors

Světlé pastelové akcenty nesou akci nebo konkrétní objekt; tmavé neutrály drží
čitelné společné pozadí. Normativní hodnoty jsou v záhlaví, původ v `tokens.css`.

### Primary

- **Mint** (`mint`, `on-mint`): hlavní tlačítko, odkazy, aktivní zákaznická
  záložka a fokus. Text na mintové ploše používá odpovídající tmavou barvu.

### Secondary

- **Jantar** (`amber`, `on-amber`): velký scénický objekt, zvýrazněná zpráva
  nebo karta; není náhradní barvou všech běžných tlačítek.

### Tertiary

- **Fialová** (`violet`, `on-violet`): alternativní scénická plocha, například
  opona. **Broskvová** (`peach`, `on-peach`) se používá také pro nebezpečnou
  akci a náhradní podobu portrétu. **Modrá** (`blue`) zůstává informačním akcentem.

### Neutral

- `bg`: pozadí aplikace; `surface` a `surface-raised`: dvě úrovně ploch.
- `text` a `muted`: hlavní a podpůrný text. `line`: nenápadné ohraničení.
- `input-bg`, `input-border`, `placeholder`: formulářová pole a jejich nápověda.
- `navigation-bg`, `navigation-muted`: podklad lišty a neaktivní odkazy.

## Typography

**Display Font / Body Font:** lokální Manrope Variable, záloha `sans-serif`.
Technické identifikátory mohou používat monospace; není to titulkové písmo.
Hierarchie v záhlaví zaznamenává sdílený aplikační základ, ne všechny lokální
nadpisy. Vzorkování vykresleného webu na 390 a 1280 px potvrdilo Manrope.

- **Headline:** výrazný proměnlivý nadpis hlavního obsahu podle záhlaví.
- **Title:** nadpis sekce nebo karty, stále ve stejné rodině písma.
- **Body:** základ obsahu; odstavce hlavního obsahu používají řádkování 1,65.
  Kořenový text a některé herní popisky dědí 1,55. Token původní předlohy
  pro 14px tělo proto nepopisuje každé aktuálně vykreslené místo.
- **Label:** tučné ovládání; navigační popisek je samostatná menší role 0,7 rem.

Stránka postavy má vlastní větší nadpis a herní obrazovka kompaktnější nadpis.
Při rozšiřování vycházej z konkrétního okolního rozhraní; neroztahuj jednu
naměřenou velikost na všechny obrazovky.

## Layout

Zákaznický obsah má jednu hlavní oblast. Spodní lišta má čtyři destinace na mobilu
i široké obrazovce. Za běžné velikosti jsou stejně široké; při zvětšení textu
delší popisek dostane potřebnou šířku, aby se nelámal uvnitř slova.
Obsah rezervuje její skutečně změřenou
výšku včetně bezpečných okrajů, nikoli pevnou výšku podle obrázku.

Zákaznický shell používá okraj `clamp(16px, 4vw, 32px)` a společný kontejner
64 rem; postava a účet mají užší čtecí oblast nejvýše 48 rem.
Základní mobilní okraj ostatních stránek adaptéru je 24 px, do šířky 360 px klesá na 16 px.
Existující shell používá hranice 720 a 1100 px; stránka představení přechází
na dvě oblasti od 760 px. Konkrétní stránka může mít vlastní kontejner:
například formulář postavy 42 rem, představení 70 rem. Nejde o nový společný limit.
Rozestupy v záhlaví jsou používaná část škály tokenů, nikoli inventář všech hodnot.

Při 200% textu zůstávají popisky čitelné a nepřekrývají se. Pole, tlačítka a položky navigace mají
nejméně 44px dotykový cíl. Pořadí a fokus ovládání zůstávají použitelné klávesnicí.

## Elevation & Depth

Základ je plochý: karty, hlavní tlačítka a dialogy v adaptéru v2 mají vypnuté
stíny. Hloubku vyjadřuje rozdíl ploch, jemná linka, obraz a opona. Starší zlaté
gradienty a stíny z kompatibilní vrstvy nejsou novou společnou normou.

## Shapes

Záhlaví zachycuje opakované poloměry polí, tlačítek, karet, panelů a velkých
scénických ploch. Hrany a obrysy jsou klidné; fotka si zachovává proporce
a ořez určuje konkrétní portrétní komponenta.

Lokální výjimky jsou skutečné, nikoli nové globální tokeny: zákaznická stránka
používá 24px karty a pilulkové hlavní tlačítko, slavnostní představení oblouk.
Základní sdílené tlačítko zůstává zaobleným obdélníkem podle záhlaví.

## Components

### Buttons

`AwButton` má hlavní, vedlejší a nebezpečnou variantu. Aplikační základ v2
používá nejméně 56px výšku; kompaktní varianty jednotlivých obrazovek nejsou
přepsáním této dokumentované výchozí podoby. Vnitřní odsazení může dědit
kompatibilní tématickou vrstvu; záhlaví zachycuje aktuální základní vzorek.

Hover mírně zesvětlí plochu. Disabled sníží opacity na 0,48 a zruší hover efekt.
Busy nastaví `aria-busy`, zablokuje tlačítko a podle motion režimu může ukázat
indikátor. Klávesnicový fokus je mintový obrys s odsazením; běžný shell má
3px obrys, zákaznická stránka místní 2px variantu.

### Cards / Containers

Sdílená karta používá `surface`, tenkou `line`, základní poloměr `card`
a odsazení z tématu. V základním vzorku je to 20 px; některé zachované varianty
používají 24 px. Karta není důvod obalit každou větu do dalšího kontejneru.

### Inputs / Fields

Textové pole má nejméně 54px výšku, tmavou výplň, 1px rámeček a roli `field`.
Písmo má alespoň 1 rem; současný kořen má 16px rem. Nápověda má vlastní barvu.
Fokus mění obrys a okraj. Disabled a `aria-invalid` přebírají sdílený stav.
`AwField` propojí popisek, nápovědu a chybu identifikátory; chyba má `role="alert"`.

### Navigation

Čtyři položky **Dílna / Hry / Památky / Já** mají skutečné ikony Phosphor,
viditelný text a označení aktivní stránky. Na mobilu je lišta přes celou šířku
u spodního okraje s horní linkou. Od 768 px je vycentrovaná, nejvýše 36 rem
široká, s odsazením 16 px od spodního okraje a zaobleným obalem. Obsah navíc
rezervuje 32 px nad její změřenou výšku. Odkazy mají nejméně
58px výšku; ikony 23 px. Aktivní text je mintový, neaktivní tlumený, ikony
zůstávají světlé s rozlišením opacity. Odsazení roste o bezpečné okraje telefonu.
Samostatné herní scény mohou lištu záměrně skrýt.

### Zákaznický přehled postavy a památek

Přehled postavy začíná jménem, světem a pravdivým stavem přípravy. Má jednu
hlavní následující akci: do dílny, nebo ke hrám, pokud už má uloženou hůlku.
Během přípravy vysvětluje čekání a průběžně obnovuje stav. Dílnu nevkládá
do přehledu jako druhou celou obrazovku. Portrét následuje za přehledem;
hůlka, rozlišovací označení a technická revize jsou v rozbalovacích podrobnostech.

Památky vedou příběhem a otevřením první položky v pořadí serveru. Popisek
„Naposledy uložená památka“ neprohlašuje datum posledního uskutečněného večera.
Hledání, filtry a obnovení jsou v následné rozbalovací správě. Stav dostupnosti
zůstává viditelný; údaje o přístupu, propojení a odpojení patří do podrobností
konkrétní památky. Na širokém kontejneru jsou další herní záznamy vedle kolekce,
na mobilu za ní. Původní oprávnění a potvrzení odpojení zůstávají závazná.

### Představení postavy

Dvoukřídlá opona a dekódovaný soukromý portrét tvoří jeden slavnostní moment.
Chybějící obrázek má náhradní podobu a pravdivý stav. Nevyvolávej novou
generaci kvůli přehrání efektu. Pravidlo deseti sekund v popředí před zobrazením
„Pokračovat“ je výslovná místní výjimka vlastníka v `PRODUCT.md`, ne obecný
časovač pro všechna tlačítka. Omezený pohyb zachovává obsah bez přesunů a škálování.

### Živý kufr

Dílna používá stejný design v2: jeden výrazný objekt, plochou jantarovou scénu
rozhovoru a fialovou scénu výroby i hotové ilustrace. Nejde o nové společné
barvy stavů. Nedostupná dílna a chyba výroby mají zvýšenou tmavou plochu.
Původní autorizovaná ilustrace kufru nebo hůlky zůstává hlavním obrazem;
prázdný prostor nenahrazuje ukázková hůlka. Pokud ilustrace ještě vzniká nebo
selže, výsledná scéna se skryje a uložený výsledek s další akcí zůstává čitelný.

Průběh má tři pojmenované fáze **Rozhovor / Výroba / Tvoje hůlka**. Aktivní
fázi vyznačuje mintová horní linka a tučný popisek, dokončenou mintová linka.
Aktuální krok má také `aria-current="step"`. Fáze vychází z uloženého stavu;
počet odpovědí ani čas nejsou odhadem procent výroby.

Na široké obrazovce je objekt vedle rozhovoru, od 850 px ve dvou sloupcích;
scéna zůstává při posunu nahoře. Na telefonu předchází ovládání, při hovoru
nebo psaní se zmenší. Stop a ukončení hovoru jsou před obsahem rozhovoru;
Stop se neskrývá za přepis ani animaci. Uložený stav a materiál předcházejí
hlavní akci, verdikt následuje za ní. V ověřeném předstartovním průchodu je
akce návratu dostupná v první obrazovce také na 320 px při běžné velikosti textu.
Při 200% textu zůstává průchod bez vodorovného přetékání, bez slibu všeho nad přehybem.

**Pohyb následuje skutečnost.** Kufr a sedm segmentů reagují jen na měřený
výstup právě přehrávané řeči, s lineární odezvou (100 ms). Mimo stav řeči je
vstupní úroveň nulová; scéna nesimuluje hlas náhodou ani nesnímá nový mikrofon.
Indikátor výroby se pohybuje pouze při potvrzené výrobě, bez odhadu zbývajícího
času. Předání scény používá příchod (650 ms) a odchod (200 ms); dekódovaná
ilustrace má vlastní příchod (850 ms). Jde o místní hodnoty `TrunkStage`,
nikoli novou společnou délku animací. Omezený pohyb zachovává plnohodnotnou
statickou scénu, obsah i okamžitě dostupné akce.

Potvrzené textové odpovědi oznamuje samostatná zdvořilá živá oblast;
streamované útržky se průběžně nepředčítají. Vlastnosti, kouzla a uložený
přepis jsou rozbalovací podrobnosti. Ilustrace má samostatné načítání, chybu
a obnovu; její nedostupnost nesmí znovu vyrábět hůlku.

Zdroje: `WandWorkshop.vue`, `TrunkStage.vue` a
[průchod dílnou](docs/magicky-kufr-workshop.md), sloučeno v `e938775`.
Browserové kontroly používají syntetická data; nedokládají živou AI ani nativní telefon.

### Souboje v Hrách

Scéna staví dvě celé hůlky proti sobě. Osobní ilustrace mají svislou kompozici
2:3, skutečnou průhlednost a rozdílné okraje; scéna je natočí hroty ke středu,
zachová rukojeť i hrot a nepřebarvuje jejich materiál. Název patří pod příslušnou
hůlku. Nedostupný obrázek zastupuje jednoduchý neutrální obrys.

Zveřejněná výměna spustí nejvýše dvousekundový střet: úder má ostrou stopu,
lest vlnivý proud a štít oblouk v místě nárazu. Jantar a fialová rozlišují
strany; světlé jádro a krátký rozlet částic patří pouze nárazu. Výsledek je
čitelný okamžitě a ovládání na efekt nečeká. Omezený pohyb ponechá celou
statickou scénu; historie ani opakované načtení stejné výměny efekt nepřehrává.

### Kaskáda a rozsah

`src/main.ts` načítá aplikační základ, starší `consumer-brand.css` a nakonec
`wns-v2.css`. Adaptér v2 mapuje `--wns-*` do sdílených `--aw-*` proměnných.
Pořadí platí při stejné specificitě; silnější selektory a lokální Vue styly
mohou výsledek upravit. Samotná existence tokenu nedokládá jeho použití.

## Do's and Don'ts

### Do:

- **Do** používej existující tokeny, místní Manrope a sdílené Vue komponenty.
- **Do** zachovej jednu dominantní scénu a jasně pojmenovanou hlavní akci.
- **Do** ověř lokální styly, bezpečné okraje, klávesnici, 200% text a omezený pohyb.
- **Do** používej skutečné autorizované údaje a správnou revizi soukromého portrétu.

### Don't:

- **Don't** zaměňuj ukázkový portrét, zvuk nebo stav za produkční výsledek.
- **Don't** zaváděj trvalý ambient, povinné částice nebo nový font místo Manrope.
- **Don't** nech herní výsledek nebo oprávnění záviset na doběhu animace.
- **Don't** přenášej lokální oblouk, pilulku nebo časovou výjimku na celý systém.

## Závazné hranice

Vue/TypeScript/Pinia/Capacitor zůstávají produkční stack. React showroom je pouze
reference. Oprávnění, passkeys, souhlasy, revize, příkazy, historie, Stop a herní
výsledky vlastní původní doména/server, nikoli animace nebo lokální čas.
Hůlky se neničí. Neexistují fyzické ceny. Převzetí památky je operace přístupu.
Rozhraní i generované texty jsou pouze česky; humor je možný kdekoli, ne povinný.

Událost pro efekt smí pocházet jen z autorizované projekce. Zvuk je výchozí
vypnutý; soukromé signály jsou tiché. Ztracený, skrytý nebo opakovaný event se
nesmí přehrát jako nová událost. Změna skóre nesmí čekat na animaci.
`presentationEffects.ts` testuje tuto hranici. `wnsSound.ts` zatím připojuje
pouze explicitní uživatelský výběr; eventový adapter není důkaz serverového streamu.

Pod pečetí je samostatná doména, nikoli nový duelový GameMode. Jediná WORLD_TRUTH
se nesmí poslat klientovi a následně skrýt CSS. EXPERIENCE, PRESENTATION a
COVERT_ACTION jsou perspektivy. Odeslaná věta není provedená akce a nepozorovaný
výsledek zůstává neznámý. FEIGN_SLEEP zachovává bdělou obranu; REST skutečně uspává.

## Evidence a rozpracovanost

Viz `docs/wns-v2-implementation.md`. Úspěšný build ani syntetický browser server
neprokazují živou AI, skutečný multiplayer, nativní export nebo bezpečnost hardwaru.

Samostatná scéna magického kufru, její přirozený GPT Live rozhovor a původ použitých
ilustrací jsou popsané v `docs/magicky-kufr-workshop.md`.
