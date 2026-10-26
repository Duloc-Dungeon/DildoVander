# Wand Night Stand — závazný profil aplikace

Aktuální rozhodnutí vlastníka, 26. října 2026. Tento dokument je zdroj pravdy
pro zaměření, jazyk, humor a vzhled mobilní aplikace a jejího webového protějšku.
Při rozporu nahrazuje starší produktové a vizuální pokyny. Technické kontrakty,
oprávnění, souhlasy a pravdivost stavů zůstávají závazné.

## Produkt a hlavní cesta

Prioritou je zákaznický zážitek na telefonu: pozvánka → postava → čekárna →
společný večer → hra a souboje → památky. Vlastník připravuje skupinu, pozvánky
a otevření večera. Účet, přístupové klíče a obnova musí být snadno dostupné.

Fyzický kufr je nyní pozastavený. Jeho připojení, dílna, diagnostika a servisní
nástroje nepatří do běžné navigace, úvodní nabídky ani zákaznického účtu.
Existující servisní kód a bezpečné ukončení už spuštěné relace nejsou tímto
rozhodnutím odstraněny. Historické hardwarové specifikace nejsou roadmapou
aktuálního mobilního rozhraní.

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

Vypravěč je klidný, lehce obřadný a hostovou výjimečností nepřekvapený.
Osobní suchý roast míří na hosta v jeho zvolené fikční roli: jeden spolehlivý
rys, rozpor nebo ambice → zdánlivá pocta → přesné snížení jeho důležitosti.
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
- Text přečti nahlas, zkrať a posuď obrat. Neotáčej hlášky při každém pollingu
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

## Vzhled: The Responsive Table

Aktuální předlohou je vlastníkem dodaný prototyp **The Responsive Table**,
Preview 2 z 26. října 2026; [uložená předloha](design-system/reference/responsive-table.png).
Tmavě zelený stůl, slonová kost, hmatově působící mosazné akce, čistá bezpatková
typografie a skutečné herní postavy tvoří jednotný profesionální celek.

- Barvy a typografii definuje [DESIGN.md](DESIGN.md), implementaci
  `mobilni-aplikace/src/application/consumer-brand.css`. Používej existující
  sémantické tokeny, lokální Inter a sdílené Vue komponenty. Žádný nový framework.
- Na telefonu jedna zřetelná hlavní akce a přehledná spodní navigace.
  Rozložení se přizpůsobuje šířce; desktop není obří roztažený telefon.
- Herní stůl a žetony tvoří ilustrativní vrstvu. Skóre, jména, zásoby, počet
  výměn a dostupné akce jsou skutečná data. Ukázkové portréty, protivník,
  statistiky a pravidla prototypu se nepřenesou jako produkční skutečnost.
- Jemné rychlé odezvy při výběru, potvrzení a výsledku; žádné blokující intro,
  náhodné konfety, přetékání nebo pohyb funkčního textu. Omezený pohyb má
  plnohodnotný statický stav. Výsledek není závislý na `animationend`.
- Pozvánka si zachovává skutečnou dvoukřídlou oponu. Pevný rám se nenatahuje,
  správný portrét je dekódovaný pod látkou. Hotový obraz, chyba a povolený
  vstup mají přednost před úvodem; nejde o důvod zadržet funkční akci.
- Ověř 320 a 390 px, širší obrazovku, 200% text, klávesnici, kontrast,
  safe areas, chyby a omezený pohyb. Browser fixture není nativní ani živý AI důkaz.

## Přednost a údržba

Tento profil nahrazuje starší hnědofialový salon, patkové titulky jako výchozí
styl celé aplikace, česko-anglické přepínání, plošný zákaz žertovného náznaku
a technické nástroje v běžné zákaznické navigaci. Staré snímky zůstávají jen
datovanou evidencí. Opona a kompatibilní identifikátory témat se zachovávají.

DV01 specifikuje samostatný živý hardwarový rozhovor. Jeho fyzické kontrakty
se nemažou, ale jeho volby dospělý/chaos se nepřidávají jako volba humoru do
mobilního onboardingu. Při změně tohoto profilu aktualizuj příslušný runtime,
testy a odkazy; nevytvářej souběžnou stylovou bibli s opačnými pravidly.
