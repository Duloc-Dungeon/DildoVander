# Souboje: rozhodnutí, odveta a jeden špatný nápad

Implementace návrhu z 29. října 2026. Změny jsou místní kandidát, nikoli
nasazení nebo důkaz vyvážení skutečnými hráči.

## Přijaté chování

- Quick zachovává Úder / Lest / Štít, tajné volby, nejvýše sedm výměn a dosavadní
  XP 10 / 6. Jiskra zvyšuje hodnotu výměny pro jejího vítěze; při remíze se
  přisazené jiskry spotřebují bez bodů.
- Trénink má tři vedené výměny: Štít proti Úderu, odpověď na Štít a vlastní
  volbu. Lze jej přeskočit a znovu otevřít. Nepřidává XP ani úkol.
- Odhalení vysvětlí protitah a doložené body/jiskry. Při hraní jsou vidět
  poslední tři odhalené výměny. Výsledek připomene skutečnou výměnu a nabídne
  odvetu. Vzájemná bilance se nevymýšlí z neúplné historie.
- Splněno / Přeskočit úkol přímo uloží skutečný stav. Odveta se zpřístupní
  po potvrzené odpovědi serveru. Nejisté odeslání dál vyžaduje ověření stavu.
- Taktika ukazuje zásoby obou stran společně. Obnova po čtyřech výměnách má
  stručné vysvětlení; jiskry se neobnovují. Výchozí 2/2/2 a dobrovolné sestavy
  3/2/1 si zachovávají stávající podmínky odemčení, bez nového pasivního bonusu.
- Taktika i quick končí při náskoku **větším** než
  `R + min(R, J1) + min(R, J2)`. Při rovnosti pokračují kvůli možné remíze.
  Vyprávění i historie obsahují pouze odehrané výměny.
- Krátká taktická historka vzniká asynchronně po výsledku. XP a odveta jsou
  dostupné okamžitě. Úloha má trvalý pronájem a samostatné číslo nároku;
  po restartu lze převzít vypršelou úlohu a starý pracovník výsledek nepřepíše.
  Chyba ponechá faktický souhrn. Hotový text je i v kronice.

## Boss

Nové pokusy mají společný základ: hráč 6 energie, Baron 5, zásoby 2/2/2
obnovované podle fází. Nováček může bez hůlky nastoupit do exhibice a osvobodit
celou skupinu. Na 30 XP získá možnost jisker; základní podmínky se nezhorší.

Síla je `1 + 2 × XP / (XP + referenceXP)`, s nezápornými XP a referencí alespoň 1.
Poškození i zbývající energie zůstávají desetinné, zaokrouhluje se jen zobrazení.
Při referenci 60 má 29 XP sílu přibližně 1,652 a 30 XP 1,667; 60 XP 2 a 120 XP 2,333.
Síla se blíží 3, takže ani extrémní XP neznamenají automatické zabití jedním
běžným zásahem. Počet zásahů k vítězství má přirozeně diskrétní prahy; tento
vzorec není potvrzené finální vyvážení.

Cíl je porazit Barona do 12 výměn. Přeživší Baron znamená prohru. Záchrana je
trvalá a další prohraný pokus mistra znovu nezajme. Uložené energie a síla
rozpracovaných pokusů se zpětně nepřepočítávají.

## YOLO: scénář, akce a protiakce vytváří AI

Po přijetí se zmrazí povolené profily obou fikčních postav a jejich skutečných
hůlek: jméno, svět, nejvýše tři aktuální doložené fakty, název hůlky, afinita,
slabina, temperament a nejvýše tři známá kouzla s krátkým popisem. Soukromý
vstupní popis postavy, štítky, zdroje, URL, účetní údaje ani fyzické identifikátory
hůlky do požadavku nevstupují.

Zápas přejde do `preparing`. První požadavek na AI vytvoří novou společnou
situaci, konkrétní cíl, předmět a místo, odlišné plány pro každého hráče,
jejich přínosy, ceny a konkrétní ochranné i útočné protiakce. Vytvoří také
vlastní závěrečné akce obou hráčů. Nejde o vyhledání v tematickém katalogu.
Připravený scénář má ID `ai-scenario-v1`; titulek, obsah i karty vytváří model.

V první verzi tvoří mechanický rámec doručení předmětu v nepoškozeném stavu.
Tři interní trasy jsou vzduchem, po zemi a přes přijímací místo. Tyto značky
neurčují text, kouzla nebo konkrétní postup: model je vymýšlí z dvojice profilů.
Názvy kouzel samy nedokládají schopnost. Mechanismus musí podpírat afinita nebo
výslovný popis známého kouzla; jinak lze použít obyčejné rekvizity.

JSON schéma a parser vyžadují úplný scénář, tři vlastní plány a tři závěrečné
možnosti na každého hráče, přínos a cenu každé karty, konkrétní pojistku a zásah
u každého plánu, přesná interní ID a omezené délky textů. Neúplný výstup se
nezobrazí jako hra. Celý validní scénář se uloží atomicky před první volbou.

1. **Plán:** každý zvolí jeden z vlastních konkrétních postupů.
2. **Komplikace:** nabídka vychází ze skutečně odhalených plánů. Hráč použije
   vygenerovanou pojistku vlastního postupu, konkrétní zásah do soupeřova
   postupu nebo přípravu své závěrečné zkratky.
3. **Zakončení:** vygenerované možnosti dokončí pojištěný plán, bezpečně ale
   pomalu obejdou slabinu, nebo zkusí zkratku s předem přiznanou podmínkou.

Každý hráč tajně potvrdí jednu možnost v každém dějství. První potvrzení
neprozradí volbu soupeři. Po druhém se trvale uloží a společně odhalí obě
volby i pevné následky podle předem známých pravidel. Mezi dějstvími není
požadavek na model. Všech šest voleb zůstává po obnovení i selhání vyhodnocení.

Druhý požadavek na AI dostane zmrazené profily, vytvořený scénář a všech šest
uložených voleb s následky. Splnění cíle je závazné: vítěz musel cíl splnit;
remíza vyžaduje dvě splnění, společné fiasko žádné. AI posoudí návaznost,
přípravu a zásahy a pojmenuje rozhodující uloženou akci. XP ani slavná postava
nejsou automatická výhoda. Technická chyba je `failed`, nikoli remíza.

Přípravná chyba opakuje pouze nedokončenou přípravu se stejnými profily.
Chyba verdiktu zachová celý scénář a šest voleb a opakuje jen verdikt. Hotový
scénář ani výsledek se nepřepisují. Během přípravy je rezervovaný aktivní slot,
funguje návrat z lobby i zrušení, také pokud mezitím zmizí způsobilá hůlka.
Historické hotové příběhy mají zachovanou čtečku. Frontend a backend této
verze se mají vydávat společně; starý klient nemá nové rozhodovací ovládání.

Oba požadavky sdílejí frontu s nejvýše dvěma YOLO pracovníky. Taktické vyprávění
má samostatného jednoho pracovníka. Souběh na produkčním provozu není změřený.
YOLO nedává bojové XP a zůstává dobrovolnou odbočkou večera.

Humor je původně český, inspirovaný Pratchettovou situační logikou: obyčejný
zájem předmětu, instituce nebo doložený rozpor postavy se vážně dotáhne do
absurdního důsledku. Příprava i verdikt pracují se skutečnými podklady. Nepřebírají
cizí hlášky, nevyrábějí soukromé vlastnosti hosta a nevkládají vtip do každého
ovládacího prvku. Strukturální validace sama nedokazuje kvalitu humoru ani
pravdivost všech významových vztahů v modelovém textu; to vyžaduje další hraní.

## Reprodukce a hranice ověření

- `dotnet build backend-tests/BackendTests.csproj`
- `dotnet run --project backend-tests/BackendTests.csproj --no-build -- --mobile-game`
- Totéž s `--mobile-game-boss`, `--mobile-game-http`, `--mobile-game-progress`.
- `npm run build --prefix mobilni-aplikace`
- `npm run test:game-design:browser --prefix mobilni-aplikace`
- `npm run test:game:browser --prefix mobilni-aplikace` a cílené duel/boss/YOLO skripty.
- `node contracts/verify-game-api.mjs`

`--mobile-game-yolo-live` je výslovně placený test. Ve výchozím nastavení má
limit sedmi požadavků: tři dvojice příprava + verdikt a jednu taktickou historku.
`GAME_DESIGN_PAID_BUDGET` může omezit počet před každým požadavkem; test se sám
neopakuje a SDK automatické retry je vypnuté. V této úloze bylo zatím použito
17 požadavků z povolených 20: deset při první iteraci a sedm pro skutečně
vygenerované scénáře, akce a verdikty. Posledních sedm prošlo pro přípravu tří
různých situací, výhru, remízu, fiasko a historku. Nejde o statistický důkaz AI.

Browserové fixture používají syntetická data a loopback. Kontrolují výběr,
potvrzení, obnovu, odhalení, výsledek, klávesnici a přetečení v 320/390/1280 px
i při 200% textu. Záznam snímkových intervalů v tomto testu měří desktopový
prohlížeč v klidu, nikoli výkon nativní aplikace či telefonů.

## Následující lidský a telefonní test

4–8 lidí, 30–45 minut: vedený trénink, quick, dobrovolná odveta po prohře,
první taktika, jedna YOLO situace a boss. U každého výsledku zaznamenat,
zda hráč vysvětlí příčinu a navrhne konkrétní změnu pro další pokus.

Pracovní kritéria: 80% vysvětlí protitahy a jiskry bez pomoci; medián quick
do dvou minut se samostatným měřením hledání soupeře, výkladu, úkolu a sítě;
většina pojmenuje taktickou ušetřenou/vyčerpanou akci. V YOLO ukáže volbu,
která změnila výsledek. Zapsat důvody odmítnuté odvety a chvíle, kdy lidé
reagují na sebe nebo jen čekají na telefon. Jde o orientační prahy.

Dva skutečné telefony přes skutečný backend musí ještě ověřit dvojí potvrzení,
odpojení, návrat, pauzu skupiny, více YOLO párů a selhání AI. Změřit latenci,
snímkové časy a paměť během opakovaných odvet. Nativní měření a tento lidský
playtest zatím neproběhly.
