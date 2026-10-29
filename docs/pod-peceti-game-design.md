# Pod pečetí — důvěra a důkazy

Aktuální cílový stav iterace z 29. října 2026. Rozhodnutí a technickou
přejímku zaznamenává [rozhodovací záznam](continuation/pod-peceti/TRUST-20261029.md),
organizaci večera [návod pilotu](pod-peceti-human-pilot.md).
Starší zprávy v `docs/continuation/pod-peceti/` jsou datované podklady.

## Zážitek a hranice

Zjistím vlastní informaci, porovnám ji s tvrzením druhého, rozhodnu se,
komu uvěřím, a podle toho změním tah. Měřítkem jsou postavy a společný večer.
Telefon podporuje rozhovor a vlastní úsudek; zdrženlivý hráč může přispět
zprávou, vlastním důkazem, ochranou i volbou cíle. Rozhovory se nemusí přepisovat.

Vlastník výslovně potvrdil, že nejsou rozehrané hry. Tato iterace zavádí jediný
nový cílový stav bez legacy režimu, migrace rozehraných partií a testů starých
checkpointů. Integritní kontrola uloženého obsahu zůstává platná.

Krátká hra (`first`) podporuje 8–12 hráčů a tři kapitoly. První lidský pilot
má osm hráčů a scénář **Společenství vlastníků krbu**. Stávající rozvrh je
5 minut úvodu, třikrát 8 minut plánování a 2 minuty tichého vyhodnocení,
10 minut finále: celkem 45 minut. Jde o formát pilotu, ne ověřené tempo
budoucí hry. Plná varianta (`full`) má šest kapitol pro 8–30 hráčů.
Scénáře používají společný engine a stejná oprávnění akcí.

Tajná strana je nezávislá na původní postavě a jejím kánonu. Počty týmů,
vítězný práh, časování, Ozvěny, graf důkazů a původní postavy zůstávají.
Při osmi hráčích jsou dva Spiklenci (ve scénáři krbu Přednostní cestující);
závěrečný seznam má dvě jména a vítězný práh vyžaduje odhalit oba.
Hůlky se neničí. Nová měna, bonus přípravy, posuzování lží, limity 10/15 zpráv
ze syntetických testů a další schopnosti nejsou součástí této iterace.

## Jedna hlavní akce

Bdělá postava v krátké hře má na obou stranách stejnou nabídku. K akci lze
přidat dosavadní sousední přesun. Pouhý výběr nebo rozhovor není přijatý plán:
server jej přijme až výslovným potvrzením s revizí a v otevřeném plánování.
Změna místního návrhu nemění už potvrzenou akci.

| Akce | Účinek | Cena a omezení |
| --- | --- | --- |
| Pátrat | Nejvýše jedna dostupná stopa podle dosavadního výběru, doručování a informačního rozpočtu. | Jeden hlavní tah. Výsledkem může být nový nález, známá stopa nebo žádná nová dostupná stopa. Žádný zaručený viník ani nález. |
| Chránit sebe | Dosavadní `Guard`, bez cizího cíle. Přítomná ochrana v dané kapitole blokuje cizí sabotáž ve stejné místnosti. | Tento tah nepátráš. Ochrana se vyhodnocuje po přesunech a před sabotáží, její časová platnost se nemění. Sama nedokládá pokus o útok. |
| Zdržet stopu | `Sabotage`, pouze `DELAY`, jedna konkrétní dostupná stopa. Její nové získávání pátráním se odloží do další kapitoly. | Tento tah nepátráš. Zásah může selhat; získané kopie zůstávají. V poslední kapitole úspěch znamená žádné nové získání této stopy pátráním před hlasováním. |

DELAY nemění kandidáty, obsah, historii ani vlastnictví dosavadních kopií.
Nejde o přesměrování jediného doručení. Dosavadní omezení opakovaného odložení,
pořadí kolidujících pokusů, místní dostupnosti zdroje a ochrany zůstávají.
Neúspěch neprozradí hlídače ani jiný skrytý stav.

Nabídky používají jen zdroje z oprávněného pohledu v současné místnosti
po uvolnění. ID zdroje nepřidává jeho obsah do vlastního spisu. Veřejný rozpis
ukazuje rodinu A/B/C, místo a kapitolu, nikoli ID, kandidáty nebo tajné odložení.
Pro volbu konkrétního zdroje C doraz před začátkem třetí kapitoly; přesun
v posledním tahu neslibuje výběr dosud neznámého zdroje v cíli.
Při krátkém DELAY zůstáváš u vybraného zdroje. Odchod by jej nechal mimo dosah;
pro cestu zvol včas jiný tah. Pátrání i ochrana si sousední přesun ponechávají.

Server vynucuje krátkou nabídku i při přímém požadavku. Nepovolenou metodu,
typ nebo cizí cíl ochrany odmítne; nepřepíše je na jinou akci.
Systémové vyčkání/fallback a dosavadní stavy ostatních postav zůstávají.
V žádné délce nelze nově potvrdit neúčinnou přípravu pátrání. Plná hra si
ponechává ostatní akce. Nová příprava je dostupná pro krádež, omráčení
a krajní čin, kde se dosavadní bonus skutečně používá. Odpočinek se nemění.

## Pravda, zkušenost a sdělení

- `WORLD_TRUTH`: pravdivý graf důkazů. Úplný vlastní řetězec A/B/C umožňuje
  správný závěr; kvůli vyváženosti se neznejasňuje.
- `EXPERIENCE`: skutečně získané vlastní stopy a uložené zkušenosti konkrétní
  akce a kapitoly. Prázdné pátrání samo neprozradí sabotáž. Chybějící podklad
  znamená neznámý výsledek, nikdy převzetí starého úspěchu.
- `PRESENTATION`: zpráva zůstává tvrzením autora i při stejném ID nebo opakování
  více hosty. Vlastní podezření není doložený důkaz. Sdílení vyžaduje adresáta
  a výslovné odeslání; rozepsání nic nepublikuje.
- `COVERT_ACTION`: soukromé plány, sdělení a oprávněná znalost spoluspiklenců
  mají dosavadní publikum. Úspěch sabotéra lze potvrdit jen jeho vlastní
  uloženou zkušeností; neúspěch neodhaluje cizí plán ani ochranu.

UI odlišuje rozepsaný návrh, přijatý plán, vyhodnocený tah a pozorovaný výsledek.
Herní stav se neodvozuje parsováním českého textu, z `TraceCreated` ani z přístupu
klienta ke globálním výsledkům. Refresh a restart uchovají přijatý plán;
opakování stejné operace nepřidává další partii nebo účinek.

Soukromý spis, role a potvrzené plány zůstávají na vlastním telefonu.
V pilotu se neukazují cizím hostům ani nesdílejí screenshotem. Informace lze
říct ústně nebo vědomě odeslat zprávou. Jde o vysvětlené společenské pravidlo,
nikoli technickou blokaci screenshotů.

## Zpracování a přejímka

Zůstává český vyšetřovací spis, Vue/TypeScript/Vite, Capacitor a ASP.NET Core.
Funkční popisy, cena, potvrzení a konečný následek jsou doslovné; situační humor
se řídí [produktovým hlasem](humor-style.md). Nevznikají nové grafické assety.
Krátký soukromý úvod a znovu dostupná pravidla nahrazují dlouhý povinný tutorial.

Automatická přejímka ověřuje politiku API, soukromí, pravdivost výsledků,
nové založení/obnovu a potvrzování i celý mobilní browserový průchod.
Neprokazuje zábavnost, lidské blufování ani nativní chování telefonu.
[Lidský pilot](pod-peceti-human-pilot.md) zůstává neprovedenou přejímkou.
