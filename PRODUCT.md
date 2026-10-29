# Wand Night Stand — závazný profil aplikace

<!-- impeccable:product-schema 1 -->

Aktuální rozhodnutí vlastníka, 26. října 2026. Tento dokument je zdroj pravdy
pro zaměření, jazyk, humor a vzhled mobilní aplikace a jejího webového protějšku.
Výchozí humor upřesněn rozhodnutím vlastníka z 29. října 2026.
Při rozporu nahrazuje starší produktové a vizuální pokyny. Technické kontrakty,
oprávnění, souhlasy a pravdivost stavů zůstávají závazné.

Struktura profilu doplněna 28. října 2026 podle stávajících podkladů a rozhodnutí
vlastníka v této úloze. Podrobná pravidla níže zůstávají součástí profilu.

## Platform

web

## Users

Hlavním uživatelem je dospělý host soukromého společného večera, který na telefonu
přijme pozvánku, připraví fiktivní postavu, zapojí se do her a vrací se k památkám.
Vlastník připravuje skupinu, pozvánky a otevření večera. Servisní a provozní role
mají vlastní oprávnění a nejsou běžnou zákaznickou cestou.

## Product Purpose

Propojit osobní fiktivní roli hosta se společným večerem: od pozvánky a představení
postavy přes hru a souboje až po uchované památky. Účet a další krok mají zůstat
srozumitelné také při čekání na přípravu nebo při chybě spojení.

## Positioning

Zážitek spojuje pozvánkovou skupinu, vlastní fiktivní postavu, osobní suchý roast,
portrét a společné hraní. Osobní text vychází z ověřeného podkladu zvolené role;
bez něj nastupuje původní vypravěčský humor, nikoli vymyšlené vlastnosti hosta.

## Operating Context

Přednost má ovládání na telefonu během přípravy a společného večera. Sdílené
Vue rozhraní běží na webu a v Capacitor obalech pro iOS a Android; označení `web`
popisuje společný jazyk rozhraní, nikoli omezení distribuce na prohlížeč.
Data účtu, přístupu a přípravy dodává backend. Lokální náhledy používají
syntetická data a nepotvrzují živou AI ani chování fyzického zařízení.

## Capabilities and Constraints

Závazné cesty a technické hranice rozvádějí oddíly „Produkt a hlavní cesta“,
„Platforma a neměnné hranice“ a „AI a odhalení“. Vývoj fyzického kufru je
pozastavený; uložené hardwarové kontrakty a bezpečné ukončení se zachovávají.
Změna vzhledu ani test neopravňují placená modelová volání, nasazení či publikaci.

## Brand Commitments

Název je Wand Night Stand. Rozhraní je české; hlasem je klidný obřadný vypravěč
s osobním suchým humorem. Podrobný jazyk a hranice humoru určují oddíly níže.
Současný vizuální systém zaznamenává [DESIGN.md](DESIGN.md); tato aktualizace
nevybírá nový výtvarný směr ani nemění jeho tokeny.

## Evidence on Hand

- [README.md](README.md) popisuje účel a hranice rozpracovaného prototypu 18+.
- [DESIGN.md](DESIGN.md), [tokeny v2](mobilni-aplikace/src/assets/wns-v2/design/tokens.css)
  a [manifest zdrojů](mobilni-aplikace/src/assets/wns-v2/source-manifest.json)
  dokládají současné vizuální zdroje; manifest není inventář produkčních účtů.
- [Stav implementace](docs/wns-v2-implementation.md) odděluje ověřené části
  od nedokončených a od nativní či živé přejímky.
- [Browser scénáře a fixtures](mobilni-aplikace/tests) jsou syntetická evidence
  chování a rozložení. Nejsou referencí reálných hostů, výsledků AI ani cen.
- Historicky uvedený soubor `design-system/reference/responsive-table.png`
  v tomto checkoutu chybí; není dostupným důkazem současné vizuální shody.

## Product Principles

1. Zákaznická cesta vede od pozvánky přes postavu ke společnému večeru a památkám.
2. Oprávnění, souhlasy a skutečný stav vlastní serverové kontrakty, nikoli animace.
3. Osobní humor používá podloženou fiktivní roli; bez podkladu zůstává neosobní.
4. Chyba přípravy nabídne pravdivý stav a další krok; nesmí předstírat hotový výsledek.
5. Sdílené české rozhraní chrání soukromé údaje a odděluje hosta od servisních nástrojů.

## Accessibility & Inclusion

Ověřuj 320 a 390 px, širší obrazovku, 200% text, klávesnici a viditelný fokus,
kontrast a bezpečné okraje telefonu. Omezený pohyb má plnohodnotný statický stav.
Funkční význam nesmí záviset na zvuku, barvě nebo dokončení animace. Vtip není
náhradou za srozumitelný souhlas, chybu ani pojmenování dalšího kroku.

## Produkt a hlavní cesta

Prioritou je zákaznický zážitek na telefonu: pozvánka → postava → kufrova dílna
přímo v aplikaci → hry a souboje → památky a žebříček. Čekání na otevření večera
zůstává součástí pozvánkové fáze. Vlastník připravuje skupinu, pozvánky
a otevření večera. Účet, přístupové klíče a obnova musí být snadno dostupné.

Fyzický kufr je nyní pozastavený. Jeho připojení, hardwarová dílna, diagnostika a servisní
nástroje nepatří do běžné navigace, úvodní nabídky ani zákaznického účtu.
Existující servisní kód a bezpečné ukončení už spuštěné relace nejsou tímto
rozhodnutím odstraněny. Historické hardwarové specifikace nejsou roadmapou
aktuálního mobilního rozhraní.

Digitální kufr v aplikaci je hlavní dílna pro přípravu hůlky hlasem nebo psaním.
Zákaznická navigace nabízí Dílna / Hry / Památky / Já. Starý odkaz
`/me/conversation` vede do `/play/wand`; nenabízí Bluetooth ani nespouští
hardwarovou relaci. Historické památky zůstávají dostupné z účtu.

## Platforma a neměnné hranice

Používej existující Vue/TypeScript/Vite aplikaci, Capacitor a nativní adaptéry,
ASP.NET Core backend a sdílené kontrakty. ESP-IDF, simulátor a CAD zůstávají
technickým zázemím. Zachovej Stop, časově omezená oprávnění, souhlasy, ruční
mazání a historické záznamy. Neznámý, simulovaný a měřený stav se neslučují.
Geometrie, měřítko a materiály CAD jsou technická data. Vizuální změna sama
neopravňuje placené modelové volání ani fyzickou akci zařízení.

## Čeština a hlas

- Rozhraní je pouze české, včetně chyb, přístupnosti a okamžitých fallbacků.
  Staré uložené `en` se vyhodnotí jako čeština; volba anglického rozhraní se
  nenabízí. Jména, značky, kódy protokolů a původní uživatelský obsah se
  nepřekládají násilně. Jazyk skutečného rozhovoru je jiná doménová volba.
- Piš původně česky, s diakritikou a přirozeným slovosledem. Tykej; `ty`, `tvoje`
  a `tobě` piš běžně malými písmeny. Titulky, akce a vysvětlení jsou krátké.
- Jméno zobrazuj samostatně v nominativu. Vokativ použij jen při jistotě.
  Rod fikční postavy neurčuje rod hosta; preferuj „máš“, „chceš“, „tvoje ego“.
- Funkční text mluví doslova: „Přijmout pozvánku“, „Postava je uložená“,
  „Zkusit znovu“. Chyba aplikace nesvádí vinu na hosta. Souhlas, chyba,
  bezpečnostní stav a další krok nesmějí být skryté ve vtipu.

## Humor celé aplikace

Výchozí směr je **pratchettovská situační logika a původní čeština podle
Kantůrkova principu**. Platí automaticky pro nové texty, úpravy, ukázky,
náhradní hlášky i zadání generátorům; uživatel ho nemusí znovu připomínat.
Před jejich psaním načti [redakční návod a rešerši](docs/humor-style.md).
Jde o náš vlastní hlas vystavěný na těchto postupech, ne o přebírání vět,
postav nebo hlášek ze Zeměplochy.

Nevymýšlej anglický vtip k následnému překladu. Nejprve najdi rozpor
konkrétní situace, pak jeho české vyjádření. Neživá věc nebo instituce může
mít zcela všední zájem a dovést ho s vážnou tváří do absurdního důsledku.
Vypravěč ten důsledek sděluje jako samozřejmost. Náhodné razítko, opona
nebo urážka na konci informační věty samy o sobě tento humor netvoří.

Vypravěč je klidný, lehce obřadný a hostovou výjimečností nepřekvapený.
Osobní suchý roast je jedním z prostředků, nikoli celou definicí stylu.
Vychází ze zvolené fikční role: jeden spolehlivý rys, rozpor nebo ambice
→ zdánlivá pocta → přesné snížení její důležitosti.
Dobrá postava nemá automaticky mírnější režim; zlá není podmínkou roastu.

Bez spolehlivého osobního podkladu použij původní vypravěčskou absurditu:
osud, čas, dějiny nebo smyšlená instituce berou svůj postup vážně a dojdou
k směšně všednímu závěru. „Neutrální“ znamená bez osobního údaje, ne bez pointy.
Používej tyto komické postupy, nekopíruj cizí hlášky ani konkrétního autora.

- Jedna premisa, jeden obrat, obvykle jedna až dvě krátké věty. Pointa patří
  na konec; bez dovysvětlení, omluvy, smajlíku a „jen si děláme srandu“.
- Česká dvojznačnost má přednost před překladem anglické hříčky. Vynech
  výplňová „magický“, „epický“ a „legendární“, pokud nenesou pointu.
- Vulgarismy a urážky jsou povolené bez hvězdiček, když zlepší rytmus nebo
  kontrast; obvykle nejvýše jeden silný výraz. Samotná nadávka není vtip.
- Osobní podklad je zvolená fikční postava a skutečně známý kontext. Nevymýšlej
  údaje o soukromí, zdraví, vzhledu či vztazích. Žádná skupinová nenávist.
- Olivanderova kocovina je příležitostný motiv, ne povinná pointa každé věty.
  Hosta nechej odmítnout volitelný podklad bez posměchu v souhlasu nebo chybě.
- Text přečti nahlas, zkrať a posuď obrat. Musí vyrůstat z daného stavu;
  pokud lze pointu beze změny přilepit na jinou obrazovku, přepiš ji nebo vynech.
  Neotáčej hlášky při každém pollingu
  a neposílej vtipy nebo tikající sekundy do opakovaných živých oznámení.

Příklady směru, nikoli povinné opakované texty:

> Sebevědomí máš na světovládu. Pro dnešek ti svěříme židli.
>
> Opona má trému. Slyšela, koho má představit.
>
> Dějiny si tě zapamatují. Mají zvláštní slabost pro průsery.

## AI a odhalení

Host v pozvánce zadá jméno a univerzum; nevolí morálku ani intenzitu humoru.
Existující příprava s potřebným souhlasem poskytne ověřený podklad. Interně
lze vybrat dva až tři rysy, ale vtip použije jeden. Nejasný kánon není jistota.
Bez podkladu přijde stabilní obecný český vtip.

Karta odhalení řadí **vtip → identita → nejvýše jeden nepřímý náznak**.
Joke má nejvýše 120 znaků, identity 120, teaser 100; součet včetně dvou
oddělovacích mezer nejvýše 250. Limity nejsou cílová délka. Tyto limity patří
kartě odhalení, ne každému provoznímu hlášení. Text se váže na správnou revizi.
Karta neopakuje hlášku z pozvánky; rešerše a interní metadata nejsou odhalením.

Selhání textu neblokuje uloženou postavu a nespouští znovu obraz. Inspirace
v editoru je výslovně místní námět, nikoli vydávaný výsledek AI. Výběr námětu
neuděluje souhlas a nic neposílá poskytovateli. Příprava, její výsledek a chyba
vždy vycházejí ze skutečného stavu.

## Vzhled a historický podklad The Responsive Table

Starší profil uváděl vlastníkem dodaný prototyp **The Responsive Table**,
Preview 2 z 26. října 2026. Uvedená cesta `design-system/reference/responsive-table.png`
v tomto checkoutu chybí. Následující popis historického podkladu není novým
schválením předlohy; současný design v2 a jeho doložené zdroje popisuje `DESIGN.md`.
Tmavě zelený stůl, slonová kost, hmatově působící mosazné akce, čistá bezpatková
typografie a skutečné herní postavy tvoří jednotný profesionální celek.

- Barvy a typografii definuje [DESIGN.md](DESIGN.md), implementaci
  `mobilni-aplikace/src/application/wns-v2.css`. Starší `consumer-brand.css` zůstává
  importovaný před adaptérem v2. Používej existující sémantické tokeny, lokální
  Manrope podle `DESIGN.md` a sdílené Vue komponenty. Žádný nový framework.
- Na telefonu jedna zřetelná hlavní akce a přehledná spodní navigace.
  Rozložení se přizpůsobuje šířce; desktop není obří roztažený telefon.
- Herní stůl a žetony tvoří ilustrativní vrstvu. Skóre, jména, zásoby, počet
  výměn a dostupné akce jsou skutečná data. Ukázkové portréty, protivník,
  statistiky a pravidla prototypu se nepřenesou jako produkční skutečnost.
- Jemné rychlé odezvy při výběru, potvrzení a výsledku; žádné blokující intro,
  náhodné konfety, přetékání nebo pohyb funkčního textu. Omezený pohyb má
  plnohodnotný statický stav. Výsledek není závislý na `animationend`.
  Aktuální implementace představení po „Vstoupit“ dovolí pokračovat při
  připravené postavě a dekódovaném portrétu. Při nedokončené přípravě nabídne
  pokračování po pěti minutách v popředí; dokončený ruční pokus o obnovu
  umožní pokračovat i bez hotového portrétu. Ověřování vstupu zůstává samostatné;
  tyto podmínky se nerozšiřují na jiné funkční akce nebo herní výsledky.
- Odhalení postavy i čekárna používají stejný nástup živého zrcadla a osobního
  textu podle volby vlastníka z 29. října 2026. Správný portrét je dekódovaný
  pod mlhou; hotový obraz, chyba a povolený
  vstup mají přednost před nepovinnými efekty; dostupnost pokračování
  určuje připravenost a ověření podle podmínek výše.
- Ověř 320 a 390 px, širší obrazovku, 200% text, klávesnici, kontrast,
  safe areas, chyby a omezený pohyb. Browser fixture není nativní ani živý AI důkaz.

## Přednost a údržba

Tento profil nahrazuje starší hnědofialový salon, patkové titulky jako výchozí
styl celé aplikace, česko-anglické přepínání, plošný zákaz žertovného náznaku
a technické nástroje v běžné zákaznické navigaci. Staré snímky zůstávají jen
datovanou evidencí. Kompatibilní identifikátory témat se zachovávají;
samostatnou oponu nahrazují vlastní animace portrétu a hůlky.

DV01 specifikuje samostatný živý hardwarový rozhovor. Jeho fyzické kontrakty
se nemažou, ale jeho volby dospělý/chaos se nepřidávají jako volba humoru do
mobilního onboardingu. Při změně tohoto profilu aktualizuj příslušný runtime,
testy a odkazy; nevytvářej souběžnou stylovou bibli s opačnými pravidly.
