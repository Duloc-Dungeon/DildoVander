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
  sealed-paper: "#f2e6c8"
  sealed-ink: "#29291f"
  sealed-muted: "#5d5b48"
  sealed-line: "#b5aa8f"
  sealed-green: "#324e3b"
  sealed-green-hover: "#243d2c"
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
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.65
  label:
    fontFamily: "'Manrope Variable', sans-serif"
    fontSize: ".95rem"
    fontWeight: 800
    lineHeight: 1.45
    letterSpacing: "-0.01em"
rounded:
  field: "14px"
  button: "16px"
  card: "22px"
  sheet: "24px"
  hero: "28px"
  sealed-dossier: "12px"
spacing:
  "4": "4px"
  "8": "8px"
  "12": "12px"
  "14": "14px"
  "16": "16px"
  "20": "20px"
  "24": "24px"
  "32": "32px"
  "40": "40px"
  "48": "48px"
components:
  button-primary:
    backgroundColor: "{colors.mint}"
    textColor: "{colors.on-mint}"
    typography: "{typography.label}"
    rounded: "{rounded.button}"
    padding: "8px 14px"
    height: "56px"
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
  notice:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.text}"
    rounded: "{rounded.field}"
    padding: "16px"
  sealed-dossier:
    backgroundColor: "{colors.sealed-paper}"
    textColor: "{colors.sealed-ink}"
    rounded: "{rounded.sealed-dossier}"
    padding: "clamp(16px, 4vw, 28px)"
  sealed-share:
    backgroundColor: "{colors.sealed-green}"
    textColor: "{colors.sealed-paper}"
    rounded: "{rounded.sealed-dossier}"
    padding: "12px 16px"
---

# Wand Night Stand — schválený design v2

## Overview

Obnoveno podle implementace 29. října 2026. Dokument zachovává současný
výtvarný směr a popisuje sdílenou aplikaci Vue pro web a Capacitor; nepřevádí
technické viewery na zákaznické téma. Zdrojové tokeny, aplikační adaptér,
skutečné komponenty a jejich místní odchylky mají přednost před historickým
mockupem. Rozšíření pro náhledy a pohyb jsou v `.impeccable/design.json`.

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

Paleta v záhlaví je společná vrstva. Ilustrace hůlky, soukromý portrét,
odznaky kroniky a herní materiály si zachovávají barvy svého obsahu; nejsou
zdrojem dalších ovládacích barev. Mintová akce ani barevné upozornění samy
nepotvrzují úspěch operace — text vždy uvádí skutečný stav.

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
  mlha zrcadla. **Broskvová** (`peach`, `on-peach`) se používá také pro nebezpečnou
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
  Záhlaví používá relativní velikost `1rem`; nevynucuje neimplementovaný
  společný 15px základ. Pole používají 1 rem, popisky a poznámky menší místní
  role. Původní tokeny předlohy obsahují škálu 10/11/14/16/17/22/36 px;
  není to příkaz přepsat všechny současné komponenty na tyto hodnoty.
- **Label:** tučné ovládání; navigační popisek je samostatná menší role 0,7 rem.

Stránka postavy má vlastní větší nadpis a herní obrazovka kompaktnější nadpis.
Při rozšiřování vycházej z konkrétního okolního rozhraní; neroztahuj jednu
naměřenou velikost na všechny obrazovky.

Baronovo představení má místní titul `clamp(2.6rem, 11vw, 4rem)`, váhu 800,
řádkování 1,02 a prostrkání −0,035 em. Jeho jméno stojí před portrétem;
nejde o nový společný nadpis. Energie používá tabulkové číslice, hodnotu
1,25 rem a menší tlumený jmenovatel 0,875 rem.

## Layout

**Pravidlo vlastníka povrchu.** Aplikační shell, zákaznický shell a jednotlivé
scény mají vlastní šířky a breakpointy. Hodnota předlohy 393 × 852 px je
referenční plátno, nikoli breakpoint nebo požadavek na rozměr telefonu.

| Povrch | Vlastník a kompozice |
| --- | --- |
| Přihlášení a pozvánka | `SignInView`: formulář a přístupové klíče, bezpečné okraje a čitelný další krok. |
| `/launch` | `LaunchView`: příprava postavy, živé zrcadlo, předání do dílny a čekání na otevření večera. |
| Představení po vstupu | `CharacterEntrance`: portrét a osobní text, od 760 px dvě oblasti, na telefonu pod sebou. |
| `/me/character` a `/me/account` | `CustomerView`: přehled role, další krok a samostatná správa účtu. |
| `/me/keepsakes` | `CustomerKeepsakes`: ilustrovaná kronika před rozbalovacím archivem a dalšími záznamy. |
| `/play/wand` | `WandWorkshopView`: rozhovor a kompaktní scéna, po dokončení vlastní animace ilustrace hůlky. |
| Hry, týmy a Pod pečetí | Vlastní herní komponenty zachovávají stav zápasu a mají místní kompaktní scény. |
| Správa a AI laboratoř | Oprávněné nástroje a tabulky používají stejnou typografii, kontrast a stavy formulářů; neproměňují se v herní scénu. |

Zákaznický obsah má jednu hlavní oblast. Spodní lišta má čtyři destinace na mobilu
i široké obrazovce. Za běžné velikosti jsou stejně široké; při zvětšení textu
delší popisek dostane potřebnou šířku, aby se nelámal uvnitř slova.
Obsah rezervuje její skutečně změřenou
výšku včetně bezpečných okrajů, nikoli pevnou výšku podle obrázku.

Zákaznický shell používá okraj `clamp(16px, 4vw, 32px)` a společný kontejner
64 rem; účet má užší čtecí oblast nejvýše 48 rem. Postava má 62 rem,
aby portrét a osobní text pokračovaly ve stejné kompozici jako na `/launch`.
Základní mobilní okraj ostatních stránek adaptéru je 24 px, do šířky 360 px klesá na 16 px.
Existující shell používá hranice 720 a 1100 px; stránka představení přechází
na dvě oblasti od 760 px. Konkrétní stránka může mít vlastní kontejner:
například formulář postavy 42 rem, představení 70 rem. Nejde o nový společný limit.
Rozestupy v záhlaví jsou používaná část škály tokenů, nikoli inventář všech hodnot.

Příprava před otevřením večera v `/launch` má vlastní kontejner (62 rem).
Postava s portrétem a dolní čekárna přecházejí na dvě oblasti od 760 px;
na telefonu se řadí pod sebe. Jméno, stav a osobní věta mají přednost před
odpočtem a volitelnými nastaveními. Nejde o náhradu zákaznického shellu.

Dílna má od 850 px dva sloupce, na telefonu jednu obsahovou osu. Mobilní
scéna kufru drží stejný kompaktní rozměr při řeči, naslouchání i psaní;
nemění se s výškou lišt prohlížeče. Průběžný přepis má vlastní posuvnou
oblast vysokou 8 rem. Výsledná ilustrace má na telefonu stálou plochu
nejvýše 14 rem na šířku, a to i při načítání nebo chybě.

Herní obsah zůstává nejvýše 520 px široký. Baronovo představení má minimální
výšku 360 px, do 360 px šířky 310 px; ve vlastním zápasu je scéna kompaktní
(210 px, na úzké obrazovce 180 px). Energetické bloky mají sloupce alespoň
8 rem a při zvětšení textu se skládají pod sebe. PvP skóre do 360 px skryje
pouze ozdobné iniciály, nikoli jména nebo body. Tyto rozměry patří herním scénám.

Při 200% textu zůstávají popisky čitelné a nepřekrývají se. Pole, tlačítka a položky navigace mají
nejméně 44px dotykový cíl. Pořadí a fokus ovládání zůstávají použitelné klávesnicí.

## Elevation & Depth

Základ je plochý: karty, hlavní tlačítka a dialogy v adaptéru v2 mají vypnuté
stíny. Hloubku vyjadřuje rozdíl ploch, jemná linka, obraz a místní optika skla. Starší zlaté
gradienty a stíny z kompatibilní vrstvy nejsou novou společnou normou.

Boss a hráčské duely mají místní světelnou clonu: oříznuté barevné paprsky,
rozostřené o 6 px na samostatné dekorativní vrstvě za postavou nebo hůlkami.
Text a ovládání zůstávají ostré. Statické nasvícení nemá vlastní běžící smyčku;
není to sklo, stín všech karet ani nový aplikační ambient.

Zrcadlo používá vlastní slabý odraz, masku mlhy a jeden odlesk. Výsledná hůlka
má samostatnou scénu s částicemi a nasvícením. Žádný z těchto nástupů se
nevrství s celoplošnou oponou; ostatní ovládání nedostává plošný skleněný filtr.

## Shapes

Záhlaví zachycuje opakované poloměry polí, tlačítek, karet, panelů a velkých
scénických ploch. Hrany a obrysy jsou klidné; fotka si zachovává proporce
a ořez určuje konkrétní portrétní komponenta.

Lokální výjimky jsou skutečné, nikoli nové globální tokeny: zákaznická stránka
používá 24px karty, slavnostní představení oblouk. Hlavní tlačítka i odkazy
stylované jako hlavní tlačítko mají společný 16px poloměr; zákaznické stránky
už nepřepisují tuto podobu pilulkou.

Portrét v živém zrcadle má poměr 4 : 5 a místní oblouk
`48% 48% 20px 20px`, s jemným jantarovým obrysem odsazeným o 7 px.
Ovládání přehrání je kruhový 44px cíl. Kronika má místní 1rem panely,
náhledy 88 × 66 px a ilustraci detailu 4 : 3. Tyto tvary nejsou novým
poloměrem pro běžné formuláře nebo dialogy.

## Components

### Buttons

`AwButton` má hlavní, vedlejší a nebezpečnou variantu. Aplikační základ v2
používá nejméně 56px výšku; kompaktní varianty jednotlivých obrazovek nejsou
přepsáním této dokumentované výchozí podoby. Vnitřní odsazení může dědit
kompatibilní tématickou vrstvu; záhlaví zachycuje aktuální základní vzorek.
Hlavní varianta používá mintovou výplň, tmavý text, 16px poloměr a žádný stín.
Stejná podoba platí pro hlavní odkazy v dílně, hrách, postavě a památkách;
starší zlatá výplň ani stín kompatibilní vrstvy se na ně nepřenášejí.

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
Aktivní položka má zvýšenou tmavou plochu a horní mintovou značku 20 × 2 px.
Detail uloženého souboje `/archive/duel` zůstává v sekci Památky; příprava
postavy patří do Dílny. Hover neaktivní položky nemění označení aktuální stránky.

### Stavové zprávy

`AwNotice` má informační, varovnou, chybovou a úspěšnou variantu. Na tmavé
ploše je čitelný text a 24px značka se skutečnou ikonou Phosphor: informace,
varování nebo potvrzovací pečeť. Barevný podklad značky používá modrou, jantar,
broskvovou nebo mintovou podle významu. Ikona je dekorativní; text a role nesou
celé sdělení. Chyba používá `alert`, ostatní živé zprávy `status`; `live=false`
živou roli vypne.

Dvousloupcové rozložení patří pouze do `AwNotice` s ikonou. Starší textové
elementy s třídou `aw-notice` zachovávají celou šířku textu bez prázdného
sloupce pro ikonu. Zpráva má 16px odsazení, 14px poloměr a 1px linku.
Potvrzení uložené hůlky má v dílně kompaktní svislé odsazení 8 px bez dalších
vnějších okrajů; na úzkém telefonu tak zachová hlavní pokračovací akci v první
obrazovce. Rozestupy mezi prvky zde vlastní mřížka rozhovoru.

### Dokončení hůlky a další krok

Uložená hůlka dostane pravdivé potvrzení s pečetí a jednu hlavní pokračovací
akci. Pečeť se krátce odhalí jen při dokončení stejného rozpracovaného rozhovoru
pozorovaném na viditelné obrazovce: 480 ms, `cubic-bezier(.16, 1, .3, 1)`.
Opakovaný polling ani návrat k hotovému výsledku efekt nespouští. Omezený pohyb
zachovává statickou pečeť i celé potvrzení; žádná akce nečeká na animaci.

Herní vstup bez hůlky používá jantarovou scénickou plochu s existující ilustrací
hůlky a další krok podle potvrzeného stavu. Dílna vede volný rozhovor hlasem
nebo psaním; rozhraní neslibuje pevný počet otázek. Prázdné památky nabízejí
hlavní akci ke hře a klidnější odkaz na žebříček.

### Povrchy prohlížeče

Výběr textu je mintový s tmavým textem, textový kurzor mintový a posuvníky
používají `line` na `bg`. Odkazy mají podtržení odsazené o .25 em. Společný
klávesnicový fokus je 3px mintový obrys s odsazením 4 px; lokální zákaznický
2px fokus zůstává zachovanou výjimkou.

### Administrace: navigace a potvrzení

`AdminNavigation.vue` propojuje účty, večer a postavy pod názvem „Zákulisí
večera“. Je to místní navigace správy, nikoli náhrada zákaznické lišty. Plochu
oddělují dvě 1px linky; odkazy mají nejméně 48px výšku, 22px ikony Phosphor
a zaoblení `field`. Aktivní stránka používá mintovou výplň, text `on-mint`
a `aria-current="page"`. Fokus má 2px mintový obrys s 3px odstupem.
Do 560 px se odkazy skládají do tří stejně širokých sloupců s ikonou nad textem.

`AdminConfirmation.vue` ukazuje pouze potvrzený výsledek zápisu. Tmavá plocha
`--aw-accent-soft`, mintový text a 36px kruh s fajfkou dávají klidnou jistotu;
text nese `role="status"`, dekorace nenese význam pro čtečku. Fajfka se odkrývá
přes `clip-path` za 240 ms s `cubic-bezier(.16, 1, .3, 1)`; omezený pohyb
ponechá statický znak. Zápis ani další ovládání na animaci nečekají.

Humor patří do úvodu nebo prázdného detailu, nikoli místo chybového hlášení.
Hledání postav bez výsledků nabízí přímo zrušení filtru; návrat načtených
postav nevyžaduje další požadavek. Neověřený výsledek obnovení seznamu se
nesmí zobrazit jako nové úspěšné potvrzení.

### Zákaznický přehled postavy a památek

Přehled postavy začíná jménem, světem a pravdivým stavem přípravy. Má jednu
hlavní následující akci: do dílny, nebo ke hrám, pokud už má uloženou hůlku.
Během přípravy vysvětluje čekání a průběžně obnovuje stav. Dílnu nevkládá
do přehledu jako druhou celou obrazovku. Portrét následuje za přehledem;
hůlka, rozlišovací označení a technická revize jsou v rozbalovacích podrobnostech.

`/me/character` používá stejný `PortraitMirror` jako příprava a vstup.
Uložený portrét se zobrazí po dekódování soukromým loaderem, bez opakovaného
automatického nástupu při každém návratu. Ruční přehrání zůstává místní;
nenačítá ani nevyrábí nový obrázek. Při omezeném pohybu se nenabízí a v pozadí
se pozastaví. Bez hotového obrázku zůstává zavřené sklo a pravdivý stav s obnovou.
Na telefonu má zrcadlo nejvýše 300 px a text následuje pod ním; od 760 px
stojí vedle textu, nejvýše 380 px široké. Osobní věta má nejvýše 28 ch,
váhu 650 a sazbu `clamp(1.4rem, 4vw, 2.1rem)`.

Památky vedou příběhem a otevřením první položky v pořadí serveru. Popisek
„Naposledy uložená památka“ neprohlašuje datum posledního uskutečněného večera.
Hledání, filtry a obnovení jsou v následné rozbalovací správě. Stav dostupnosti
zůstává viditelný; údaje o přístupu, propojení a odpojení patří do podrobností
konkrétní památky. Na širokém kontejneru jsou další herní záznamy vedle kolekce,
na mobilu za ní. Původní oprávnění a potvrzení odpojení zůstávají závazná.

### Ilustrovaná kronika a památky

`CustomerChronicle` je první obsah památek, před rozbalením staršího archivu.
Časová osa a detail mají na široké obrazovce sloupce 0,9 : 1,1; do 48 rem
se skládají pod sebe a filtry se ovládají přes vlastní rozbalení. Detail je
na široké obrazovce přichycený 1 rem pod horním okrajem, na telefonu statický.
Položka má nejméně 76px výšku a ilustrace detailu nepřesahuje 34 rem.

Uzamčený záznam ukazuje pouze bezpečný název stavu. Otevřený záznam zobrazuje
skutečný název, účastníky a dostupnou ilustraci. Prázdná kronika, načítání,
chyba, odpojení i změna přihlášení mají samostatnou zprávu; chybějící
ilustrace má náhradu a odznak není automaticky přidělen jen kvůli vzhledu.
Typ události, postava a odznak filtrují serverové záznamy. Starší archiv
zachovává svoje hledání, soukromí a přístupové akce v následném rozbalení.
`/play/chronicle` vede do `/me/keepsakes`, nikoli do druhé souběžné kroniky.

`ChronicleMatchReveal` je místní upozornění po zápase: odkaz na potvrzenou
novou vzpomínku, mintový akcent a normální klávesnicový fokus. Nejde o
další překryvnou scénu ani o důkaz skutečného hostování ilustrace.

### Představení postavy

Představení i čekárna používají společné živé zrcadlo (`PortraitMirror`):
mlha se za 1,1 sekundy vyčistí od středu a sklem jednou přejde odlesk.
Časovou osu řídí `motion-v` 2.9.0, v pozadí se pozastaví; hotový portrét
ani polling ji neopakují. Sdílený `CharacterIntroduction` uvádí stejnou
osobní větu a krátký přechod textu; při omezeném pohybu zůstává statický.
Sklo má jemný stálý odraz, zamlžení drobné kapky a jednorázový užší odlesk.
Po odhalení lze tlačítkem na zrcadle zopakovat obraz i stejnou osobní větu.
Přehrání nemění uloženou postavu, stav prvního odhalení ani přístup k další
akci; nevyvolává nové načtení nebo generaci. Při omezeném pohybu a chybějícím
portrétu se ovládání nepřehratelného efektu nenabízí.
Při skutečné přípravě či načítání portrétu mlha pomalu mění světlo a polohu
v cyklu 4,6 sekundy. Pohyb nepředstírá postup výroby; při načtení obrázku nebo
chybě končí, v pozadí a mimo záběr je pozastavený. Omezený pohyb a pomalé
obnovování obrazovky používají statickou mlhu.
Chybějící obrázek má náhradní podobu a pravdivý stav. Nevyvolávej novou
generaci kvůli přehrání efektu. Současný průchod nabízí „Pokračovat“ při
připravené postavě a dekódovaném portrétu. Při nedokončené přípravě umožní
pokračování po pěti minutách v popředí; po dokončeném ručním pokusu o obnovu
i bez hotové přípravy. Ověřování vstupu zůstává samostatným stavem čekání.
To popisuje aktuální `CharacterEntrance`, nikoli nový časovač pro jiné akce.
Omezený pohyb zachovává obsah bez přesunů a škálování.

| Stav portrétu | Zobrazení a přechod |
| --- | --- |
| Příprava nebo stahování | Zavřené sklo, osobní či náhradní věta a pomalu proměnlivá mlha. Bez procent nebo odhadu zbývajícího času. |
| Mimo záběr / aplikace v pozadí | Smyčka mlhy stojí; v popředí pokračuje. Čtení a dostupnost serverových akcí neurčuje tato animace. |
| První dekódovaný portrét | Mlha se vyčistí od středu a přejde odlesk. V čekárně se první odhalení zaznamená až po dokončení nad správnou revizí a alespoň polovinou viditelné scény. |
| Přerušené první odhalení | Nedokončená revize zůstává neviděná a může se přehrát znovu. |
| Známá dokončená revize / polling | Portrét a text zůstávají klidné; stejná odpověď nezačne nový nástup. |
| Ruční přehrání | Jen místní obraz a stejný text; žádná nová žádost o portrét či generaci. Klávesnicový fokus zůstává na ovládání; potřebný posun přiblíží zrcadlo do záběru. |
| Chybějící obrázek / chyba | Ikona role, pravdivá zpráva a dostupná obnova; nekonečná animace přípravy končí. |
| Omezený pohyb / pomalé obnovování | Statické sklo a výsledek; bez smyčky, přesunů a ovládání přehrání neaktivního efektu. |

`CharacterIntroduction` má příchod 480 ms, nejvýše 8px posun a 1px rozostření,
odchod 180 ms. Klíč obsahu mění pouze jiná věta nebo výslovné přehrání.
`PortraitCurtain` zůstává názvem vlastníka kompatibilních hooků
`data-curtain`; látkovou ani celoplošnou oponu už nevykresluje.

### Příprava postavy a předání do dílny

`LaunchView` používá klidný čtyřdílný přehled přípravy: hotový krok je mintový,
aktuální jantarový a budoucí tlumený. Stav označuje také `aria-current="step"`;
vynechaný portrét má výslovný popisek. Přehled není procentní ukazatel ani
časový odhad a při otevřené dílně ustoupí jejímu hlavnímu ovládání.

`PrelaunchWand` předává hlavní akci mintovou plochou s tmavým textem,
klidnými rohy (16 px) a odsazením `clamp(20px, 4vw, 32px)`. Tmavé tlačítko
s mintovým textem obrací barvy běžné primární akce; fokus je zde tmavý,
aby zůstal čitelný na mintové. Od 760 px stojí akce vedle textu,
na mobilu pod ním. Tato barevná inverze je místní vzor předání do dílny,
nikoli nová globální varianta všech karet.

Osobní věta vedle portrétu má výraznou sazbu (`clamp(1.4rem, 5vw, 2.1rem)`,
váha 650, řádkování 1,4) a krátkou čtecí míru (28 ch). Stav přípravy je
samostatný, aby vtip nezastoupil informaci nebo chybu. Společné zrcadlo
popisuje oddíl „Představení postavy“. Náhled hůlky patří `TrunkStage` a po
dekódování správné ilustrace používá vlastní `WandRevealField` (4,2 s).
Jeho pevná vrstva, světlo a částice jsou jedním nástupem; žádná další opona
se přes ni neotevírá. Omezený pohyb, neviditelný dokument, nedostupný canvas
nebo opakované pomalé snímky ponechají klidný obsah. Odměřený průběh efektu
nenahrazuje skutečný stav výroby a nedoplňuje chybějící ilustraci.
Čekárna po dokončení uchovává osobní větu i přístup k uložené hůlce;
přehrání scény samo nevyrábí nový obsah.

### Živý kufr

Dílna používá stejný design v2: jeden výrazný objekt, plochou jantarovou scénu
rozhovoru a fialovou scénu výroby i hotové ilustrace. Nejde o nové společné
barvy stavů. Nedostupná dílna a chyba výroby mají zvýšenou tmavou plochu.
Původní autorizovaná ilustrace kufru nebo hůlky zůstává hlavním obrazem;
prázdný prostor nenahrazuje ukázková hůlka. Pokud ilustrace ještě vzniká nebo
selže, její stálá plocha zobrazí pravdivý stav a dostupnou obnovu. Rozložení
se při příchodu obrázku nepřeskupí a hlavní akce zůstává nezávislá na ilustraci.

Průběh má tři pojmenované fáze **Rozhovor / Výroba / Tvoje hůlka**. Aktivní
fázi vyznačuje mintová horní linka a tučný popisek, dokončenou mintová linka.
Aktuální krok má také `aria-current="step"`. Fáze vychází z uloženého stavu;
počet odpovědí ani čas nejsou odhadem procent výroby.

Na široké obrazovce je objekt vedle rozhovoru, od 850 px ve dvou sloupcích;
scéna zůstává při posunu nahoře. Na telefonu je objekt vedle krátkého stavu
ve stejně velké kompaktní scéně při naslouchání, řeči i psaní. Průběžný přepis
má vlastní posuvnou oblast a s přibývajícími slovy nezvětšuje stránku. Pokud
host čte starší část přepisu, automatický posun mu ji nevezme.

Stop řeči, mikrofon, ukončení hovoru a stav řeči tvoří společnou skupinu pod
přepisem. Mají stálá místa i při střídání naslouchání a řeči; Stop je během
naslouchání neaktivní. Skupina nepřekrývá obsah při posouvání stránky.
U hotové hůlky není samostatné tlačítko pro přehrání verdiktu.
Uložený stav a materiál předcházejí
hlavní akci, verdikt následuje za ní. V ověřeném předstartovním průchodu je
sekundární návrat v záhlaví dostupný v první obrazovce také na 320 px. Hlavní
pokračovací akce pod větší svislou ilustrací může vyžadovat posun; zůstává
dosažitelná klávesnicí i dotykem a nikdy nečeká na animaci.
Při 200% textu zůstává průchod bez vodorovného přetékání, bez slibu všeho nad přehybem.

**Pohyb následuje skutečnost.** Kufr a sedm segmentů reagují jen na měřený
výstup právě přehrávané řeči, s lineární odezvou (100 ms). Mimo stav řeči je
vstupní úroveň nulová; scéna nesimuluje hlas náhodou ani nesnímá nový mikrofon.
Indikátor výroby se pohybuje pouze při potvrzené výrobě, bez odhadu zbývajícího
času. Kufr používá příchod (650 ms) a odchod (200 ms); hotový výsledek má
jen místní změnu krytí (180 ms), bez posunu nebo ořezu. Dekódovaná ilustrace
má vlastní jednorázové odhalení (4,2 s). Jde o místní hodnoty `TrunkStage`,
nikoli novou společnou délku animací. Omezený pohyb zachovává plnohodnotnou
statickou scénu, obsah i okamžitě dostupné akce.

Hotová ilustrace má větší svislou scénu 2:3, podle produkčního obrazového
promptu. Po načtení se skutečná autorizovaná ilustrace představí uprostřed
celého okna: podélné světlo, tři vzestupná vlákna, jemné částice a uložený
název. Motion vede jednu sekvenci a nakonec hůlku plynule usadí do její
skutečné karty. Celá sekvence trvá 4,2 s. Žádné kruhové ciferníky ani radiální
paprsky. Neprůhledné úvodní zakrytí je samostatná DOM plocha, nikoli vymazávaný
Canvas; pevná vrstva má přesah 2 px a její poloha se neanimuje.
Dotyk nebo kurzor pouze vychýlí probíhající vlákna; po uklidnění nic nepřehraje.
Dekorativní vrstva nemění obrazový soubor, nespouští výrobu ani nový zvuk.
Canvas používá nejvýše 72 částic pod 600 px a 112 na širším okně, hustotu
vykreslení nejvýše 1,5. Při pomalém vykreslování zůstane statický výsledek.
Posun nebo změna velikosti okna efekt ukončí, aby se neusazoval do staré pozice.
Skrytí stránky, odchod scény z výřezu i omezený pohyb ji ihned zastaví;
chybějící Canvas ponechá úplný statický výsledek. Nevzniká nový zvuk.

Potvrzené textové odpovědi oznamuje samostatná zdvořilá živá oblast;
streamované útržky se průběžně nepředčítají. Vlastnosti, kouzla a uložený
přepis jsou rozbalovací podrobnosti. Ilustrace má samostatné načítání, chybu
a obnovu; její nedostupnost nesmí znovu vyrábět hůlku. Dokončení přesune
fokus na název hůlky bez automatického posunu celé stránky.

Zdroje: `WandWorkshop.vue`, `TrunkStage.vue` a
[průchod dílnou](docs/magicky-kufr-workshop.md), sloučeno v `e938775`.
Browserové kontroly používají syntetická data; nedokládají živou AI ani nativní telefon.

### Souboje v Hrách

Scéna staví dvě celé hůlky proti sobě. Osobní ilustrace mají svislou kompozici
2:3, skutečnou průhlednost a rozdílné okraje; scéna je natočí hroty ke středu,
zachová rukojeť i hrot a nepřebarvuje jejich materiál. Název patří pod příslušnou
hůlku. Nedostupný obrázek zastupuje jednoduchý neutrální obrys.
Rychlý a taktický duel, YOLO i 2 na 2 používají skutečnou schválenou ilustraci
soupeře přes autorizovanou cestu konkrétního zápasu a revize. YOLO ukazuje
hůlky staticky vedle příběhu, bez domyšlené výměny kouzel. Změna soupeře,
revize, účtu nebo odvolání obrázku odstraní předchozí ilustraci.

Zveřejněná výměna spustí nejvýše dvousekundový střet: úder má ostrou stopu,
lest vlnivý proud a štít oblouk v místě nárazu. Jantar a fialová rozlišují
strany; světlé jádro a krátký rozlet částic patří pouze nárazu. Výsledek je
čitelný okamžitě a ovládání na efekt nečeká. Omezený pohyb ponechá celou
statickou scénu; historie ani opakované načtení stejné výměny efekt nepřehrává.

### Baron Šarlatán

`GameBossStage` používá původní průhledný portrét se třemi výrazy. Představení
ukazuje velké jméno, pravdivý stav Ollivandera a hlavní akci. Podmínky souboje
jsou v rozbalitelném detailu; existující pokus nabízí pokračování.
Scéna po odhalení neskrývá ovládání a nečeká na doběh efektu.

Nasvícení zápasu určuje skutečná fáze ze serveru: první fialová, druhá jantarová,
třetí broskvová. Stejnou fázi pojmenuje text a označí `aria-current="step"`.
Barva sama nenese stav. Změna fáze přehraje pouze clonu (1,1 s) a náběh místního
světla (0,7 s); neremountuje portrét ani nerozhodnutou volbu kouzla.

`GameBossEnergy` ukazuje skutečnou zbývající energii: hráč nejvýše 6,
Baron nejvýše 5 také v exhibici. Hodnoty mají český číselný formát, přístupné
ukazatele `role="meter"` a vodorovné pásy s odezvou 0,35 s. Neznámá hodnota
je pomlčka, nikoli domyšlený stav. Nápověda, Baronova jiskra, výsledek poslední
výměny a možnost ukončit aktivní pokus zůstávají čitelné bez animace.

### Baronův text a hlas

`GameBossNarration` při změně revize souboje načítá odpovídající text.
Načítání, chyba textu a chyba zvuku jsou oddělené. Hlas má výslovné zapnutí
a respektuje společné vypnutí zvuku; skrytý dokument, stará revize či
odchod z komponenty ruší staré přehrávání. Jde o podporu čitelného komentáře,
nikoli další vizuální signalizaci skóre nebo podmínku dalšího tahu.

### Duely mezi hráči

`game-duel-scene` platí pro aktivní a dokončené rychlé a taktické duely,
lokální trénink a samostatně upravenou týmovou scénu.
Hráč má jantarovou, soupeř fialovou stranu; stejné barvy propojují iniciály,
hůlky a výsledek zveřejněné výměny. Skóre používá skutečná herní jména a body.
Počet výměn a průběh stojí na jednom pružném řádku jako u bosse.

Volba kouzla zůstává soukromá. `GameClash` zvýrazní vítěznou stranu teprve,
když jsou zveřejněna obě platná kouzla. Krátké světelné dosednutí (0,75 s)
používá stávající životní cyklus střetu, nikoli další časovač. Při uzamčeném
tahu světlo obou stran zeslábne a scéna výslovně oznámí čekání.
Text „Tvoje výměna.“, „Soupeřova výměna.“ nebo „Nerozhodná výměna.“
vysvětluje stejný stav jako světlo; konečný výsledek zápasu vlastní samostatně server.

Omezený pohyb ponechá v obou scénách úplný statický stav. PvP efekt se
nepřehrává při načtení historie, stejném pollingu, skrytém dokumentu nebo
mimo viditelnou scénu. Herní pravidla, souhlasy a preference zvuku se nemění.
YOLO tento místní výtvarný režim nepřebírá.

Trénink zůstává označený „bez XP“. Stejná scéna a slovní verdikt reagují
na skutečně odehranou lokální výměnu; nevytvářejí herní API zápisy ani odměnu.
Nový trénink vynuluje místní skóre i nasvícení výsledku.
Soupeř v tréninku má výslovně označenou „Cvičnou hůlku“ z existující fialové
ilustrace. Její diagonální kompozice se natočí hrotem ke středu scény.

**Pravidlo viditelného důsledku.** Odhalení pojmenuje protitah a skutečné
body včetně doložených jisker. Při další volbě zůstávají přímo viditelné
poslední tři odhalené výměny. Výsledek připomene skutečnou výměnu a vede
k odvetě; nevymýšlí bilanci soupeřů z neúplné historie.

Trénink má tři vedené výměny, jasný další krok a možnost přeskočení i opakování.
Taktika zobrazuje zásoby obou stran ve společné tabulce. Strany odliší barva
i jméno v záhlaví; číslice jsou tabulkové. Obnova zásob má stručnou stavovou
větu. Tyto informační oddíly používají čárové oddělení místo dalších vnořených
karet, mezery ze společné škály a nadpisy (1,125 rem).

Úkol po prohře má přímé akce „Splněno“ a „Přeskočit úkol“. Zápis stavu
potvrdí server před zpřístupněním odvety. Taktická historka je samostatný
oddíl; připravovaný i neúspěšný text ponechají výsledek, XP a odvetu dostupné.
Zvuk výběru a potvrzeného tahu respektuje vypnutí zvuku i skrytý dokument;
polling ani pouhé načtení historie nevytvářejí další místní potvrzení.

### Týmová hra 2 na 2

`TeamView` ukazuje oba týmy po dvou jménech, skutečné týmové skóre a potvrzení
tahu každého hráče. Kanonické pořadí `allFourLocked` (A0, A1, B0, B1) se
překládá podle strany diváka. Neznámý stav není nehotový tah ani domyšlený počet.
Přestávka zachovává serverový termín, předání jiskry a zřetelné potvrzení;
scéna střetu v ní uvolní místo těmto ovládacím prvkům.

Jednorázový střet zobrazuje pouze osobní dvojici zveřejněné výměny. Její
známá cyklická pravidla určují světlo, zatímco týmové body i konečný výsledek
zůstávají serverové. Popisek odděluje poslední dvojici od následujícího soupeře.
Publikovaná kouzla B už mají po přestávce pořadí dvojic; toto pořadí se nesmí
zaměnit s kanonickým pořadím jmen a potvrzených tahů.
Týmová projekce poskytuje `opponentWands` podle kanonických ID obou soupeřů.
Scéna vybere jméno i autorizovaný obrázek stejné osobní dvojice, kterou ukazuje
zveřejněná výměna. Potvrzení nové dvojice se nesmí spojit s ilustrací té staré.
Chybějící nebo odvolaný obrázek zastoupí neutrální obrys; soukromé URL jiných
domén se nepoužívá. Načtená výměna zůstává statická stejně jako v duelu.

### Pod pečetí: vyšetřovací spis

Deník má lokální papírovou plochu (`#f2e6c8`) s inkoustovým textem
(`#29291f`) a tmavě zelenými doloženými částmi (`#324e3b`). Zachovává Manrope
a tmavý zákaznický shell. Nejde o změnu společných tokenů. Řetězec propojuje
části A/B/C linkou; chybějící část má přerušovaný obrys a výslovný popisek,
takže význam nenese samotná barva. Tvrzení spoluhráčů zůstávají mimo papírový
soubor vlastních dokladů.
Přehled tvrzení ukazuje autora, rozsah sdílení, odkaz na část řetězce a vlastní
ověření hned. Úplný původní text je pod klávesnicově přístupným rozbalením;
dlouhá zpráva s více stopami tak nezahltí všechny řádky. Ovládání vlastních
stop má pro čtečky obrazovky doplněný kontext příslušného řetězce.

Drobné popisky částí používají 0,8125 rem. Rozbalení vlastní stopy má
nejméně 52 px, rozbalení tvrzení 44 px a tlačítko sdílení 48 px. Názvy
opakovaných tvrzení obsahují pro čtečku i pořadí v přehledu. Fokus ovládání
používá `--wns-focus-ring`: na světlém spisu místní `#324e3b`, na tmavém
okolí výchozí mintovou. Zelená při hoveru tlačítka přechází na `#243d2c`.

Otevření spisu má jeden místní `clip-path` přechod 280 ms; při omezeném
pohybu je obsah ihned statický. Ovládání ani herní stav na efekt nečekají.
Veřejný plán průzkumu používá čitelný seznam tras a kapitol, bez zobrazení
cizích skrytých poloh nebo událostí. Místní styly vlastní
`src/features/sealed-case/sealedDossier.css` v mobilní aplikaci.

### YOLO lóže ve třech dějstvích

`GameYoloStage` a `YoloDecisionStage` rozlišují přípravu, vlastní rozhodování
a čtení hotového příběhu. Jména obou soupeřů zůstávají nad obsahem; ukázkové
hůlky jsou označené také textem. Stejný Manrope a jantarová/fialová dvojice
navazují na společný systém v2.

**Pravidlo nabídky před efektem.** Při rozhodování ustoupí ilustrace střetu
i portrét Křivopíska. Hlavním obsahem je společná situace, zvýrazněný cíl,
aktuální dějství a tři vlastní možnosti hráče. Každá karta má konkrétní název,
viditelnou výhodu a cenu. Výběr zůstává místní do akce „Potvrdit plán“.

Karty možností používají povrch, tenký obrys a místní zaoblení (12 px).
Vybraná karta má jantarový obrys a jemně tónované pozadí; označení výběru
nese také nativní radio. Fokus používá společný token s odsazením (3 px).
Cena zůstává čitelná tlumenou barvou. Zablokovaný výběr nelze potvrdit znovu;
uzamčení a čekání na soupeře vysvětlí text. Stav se nespoléhá pouze na barvu.

AI tvoří situaci, názvy akcí, protiakce a jejich ceny z obou postav a skutečných
hůlek. Rozhraní před první volbou ukáže pravdivou přípravu bez předstíraného
procenta postupu. Poté mají dějství názvy Plán → Komplikace → Poslední špatný
nápad. Odhalené volby a následky jsou při rozhodování přímo dostupné; od šířky
700 px stojí obě strany vedle sebe, na telefonu pod sebou. Při generování
verdiktu a čtení jsou uložené plány pod rozbalením, aby neodsunuly hlavní stav.

Hotový příběh vrací autorizované hůlky a malý portrét Křivopíska. Chybějící
obrázek zastoupí původní neutrální obrys. Tempo určuje čtenář tlačítkem
„Další část“; celý výsledek lze otevřít rovnou. Prostřední část má jediný
světelný střet v SVG (0,9 s), bez trvalého ambientu. Omezený pohyb ponechá
statický střet. Ovládání ani serverový výsledek na efekt nečekají. Obnovení
stejného zápasu pollingem nepřeskakuje dějství a efekt nespouští znovu.

Obsah scény má nejvýše 760 px a od 700 px používá boční odsazení 32 px.
Nadpis dějství má vlastní `clamp(1.75rem, 6vw, 2.5rem)`; obecný herní nadpis
jej nesmí přepsat. Text příběhu má 1,125 rem, řádkování 1,75 a míru do 65 ch.
Po přechodu klávesnicí nadpis ukáže fokus v barvě společného tokenu.
Označení „· ukázka“ zůstává při zalomení pohromadě.

Verdikt, zvýraznění vítězné strany, odveta a případný úkol poraženého přicházejí
až s rozsudkem. Nové scénáře výslovně pojmenují i remízu nebo společné fiasko.
Čtečka dostane oznámení o připraveném příběhu bez výsledku; ruční přechod
přenese fokus na nadpis. Příprava scénáře, příprava verdiktu, chyba s opakováním
a zrušení jsou samostatné pravdivé stavy. Zrušená ani neúplná odpověď se
nepřehrává jako dokončený příběh. Historické příběhy zachovávají dějství
Nástup → Střet → Rozsudek. AI vyhodnocuje uložené plány a jejich následky;
režim nepřiděluje bojové XP. Obě informace zůstávají přímo ve scéně.

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
- **Don't** vrstvi oponu na vlastní animaci hůlky či portrétu; používej jejich skutečné nástupy.
- **Don't** předstírej postup přípravy procenty nebo pohybem, když už běh skončil chybou.

### Závazné hranice

Vue/TypeScript/Pinia/Capacitor zůstávají produkční stack. React showroom je pouze
reference. Oprávnění, passkeys, souhlasy, revize, příkazy, historie, Stop a herní
výsledky vlastní původní doména/server, nikoli animace nebo lokální čas.
Hůlky se neničí. Neexistují fyzické ceny. Převzetí památky je operace přístupu.
Rozhraní i generované texty jsou pouze česky; humor je možný kdekoli, ne povinný.

Událost pro efekt smí pocházet jen z autorizované projekce. Zvuk je výchozí
vypnutý; soukromé signály jsou tiché. Ztracený, skrytý nebo opakovaný event se
nesmí přehrát jako nová událost. Změna skóre nesmí čekat na animaci.
`presentationEffects.ts` testuje tuto hranici. `wnsSound.ts` připojuje explicitní
uživatelský výběr a potvrzení tahu po úspěšné odpovědi nebo místním tréninkovém
vyhodnocení; eventový adapter není důkaz serverového streamu.

Pod pečetí je samostatná doména, nikoli nový duelový GameMode. Jediná WORLD_TRUTH
se nesmí poslat klientovi a následně skrýt CSS. EXPERIENCE, PRESENTATION a
COVERT_ACTION jsou perspektivy. Odeslaná věta není provedená akce a nepozorovaný
výsledek zůstává neznámý. FEIGN_SLEEP zachovává bdělou obranu; REST skutečně uspává.

### Evidence, zdroje a údržba

Viz `docs/wns-v2-implementation.md`. Úspěšný build ani syntetický browser server
neprokazují živou AI, skutečný multiplayer, nativní export nebo bezpečnost hardwaru.

Samostatná scéna magického kufru, její přirozený GPT Live rozhovor a původ použitých
ilustrací jsou popsané v `docs/magicky-kufr-workshop.md`.


Audit vychází ze skutečných implementací a zachovává platné lokální výjimky:

| Zdroj | Vlastnictví |
| --- | --- |
| `mobilni-aplikace/src/assets/wns-v2/design/tokens.css` | Základní paleta, škály, dotykové cíle a pohyb předlohy. |
| `mobilni-aplikace/src/application/wns-v2.css` | Aplikační mapování, tlačítka, formuláře, navigace a společný fokus. |
| `mobilni-aplikace/src/application/application.css` a `consumer-brand.css` | Základ a kompatibilní vrstva; pořadí importů a specificita stále platí. |
| `mobilni-aplikace/src/application/router.ts` | Skutečné cesty, přesměrování a vlastníci obrazovek. |
| `mobilni-aplikace/src/components/scene/PortraitMirror.vue` | Materiál skla, čekání, maska, odlesk a přehrání. |
| `mobilni-aplikace/src/features/launch/CharacterIntroduction.vue` | Sdílený osobní text a jeho jednorázový pohyb. |
| `mobilni-aplikace/src/features/launch/CharacterEntrance.vue` a `PortraitCurtain.vue` | Stav první prezentace, dostupnost pokračování a viděné revize. |
| `mobilni-aplikace/src/features/game/TrunkStage.vue` a `WandRevealField.vue` | Skutečný hlasový výstup, dekódovaná hůlka a její vlastní nástup. |
| `mobilni-aplikace/src/features/customer/CustomerChronicle.vue` | Časová osa, detail, filtry, ilustrace a odznaky. |
| `mobilni-aplikace/src/features/game/GameBossNarration.vue` | Revizovaný text a volitelný hlas. |

Při další obnově porovnej celé médium i hlavní lokální výjimky; neodvozuj
vzhled celé aplikace z jedné obrazovky. Aktualizuj zároveň doprovodný soubor
`.impeccable/design.json`: primitivní hodnoty zůstávají v záhlaví tohoto
dokumentu, doprovodný soubor drží pohyb, breakpointy a samostatné ukázky.
Ukázky v panelu jsou dokumentační, bez soukromých portrétů či volání providerů.

Pro změny portrétu spusť `npm run test:mirror-reveal:browser --prefix mobilni-aplikace`.
Další dotčené průchody vlastní `test:character-entrance:browser`,
`test:prelaunch-wand:browser` a `test:launch:browser` ve stejném balíčku.
Nejprve sestav aktuální web přes `npm run build --prefix mobilni-aplikace`;
scénáře používají jeho skutečný výstup a syntetický backend.
Screenshot nebo úspěšný test není potvrzení nativního telefonu ani živých
AI služeb. Vizuální dokumentace neuděluje oprávnění k nasazení.
