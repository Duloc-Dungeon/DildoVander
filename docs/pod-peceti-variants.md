# Pod pečetí: scénáře nad společnými pravidly

## Co se vybírá

Pod pečetí má samostatný výběr **scénáře** a **délky hry**. Scénář určuje
zápletku, názvy stran, místa, podobu stop a vyprávění. Dosavadní volby
`first` a `full` dál určují délku, počet kapitol a způsob zadávání akcí.
Nový scénář nepotřebuje kopii herního enginu ani samostatné účty postav.

| Scénář | Identifikátor | Strany | Rozpor, ze kterého příběh vychází |
| --- | --- | --- | --- |
| Případ posledního zvonu | `POD_PECETI_POSLEDNI_ZVON_V1` | Strážci a Spiklenci | Původní narušení ochranné pečeti. Výchozí scénář pro dosavadní klienty a partie. |
| Smrtijedi a bystrozorové: Případ zamčeného letaxu | `HP_SMRTIJEDI_BYSTROZOROVE_V1` | Bystrozorové a Smrtijedi | Úkryt má chránit tajnou trasu, ale její ochranu už někdo zevnitř změnil. |
| Společenství vlastníků krbu | `HP_SPOLECENSTVI_KRBU_V1` | Sousedé a Přednostní cestující | Všichni chtějí spravedlivý pořadník společného krbu, nejlépe s vlastní předností. |

Oba nové scénáře mají verzi `1.0.0` a univerzum `Harry Potter`. Jsou původní
herní fikcí, nikoli tvrzením o událostech z knih. Zachovávají jméno, univerzum
a uložený profil každé zvolené postavy. Přidělená strana platí pro tento případ;
neodvozuje se z kánonické pověsti, schopností, vzhledu ani morálky postavy.

## Jak založit partii

Zakladatel vybere scénář, délku hry a podporovaný počet účastníků. Lobby ukáže
zápletku před souhlasem se scénářovou minulostí. Účastníci pak používají
dosavadní výběr postavy, připravenost a souhlas. Konkrétní tajné přiřazení
stran zůstává dostupné jen oprávněným hráčům; veřejný katalog obsahuje
pouze popis prostředí a pravidel.

Po odhalení výsledku může zakladatel zvolit **Založit další případ** a vybrat
jiný scénář nebo délku hry. Vznikne nová partie; dokončený případ a jeho
rekonstrukce zůstávají zachované. Pokud předchozí pokus o založení nemá
ověřenou odpověď, aplikace nejdříve nabídne ověření téhož požadavku se stejným
scénářem, délkou a počtem hráčů.

`first` zůstává prvním případem se třemi kapitolami pro 8–12 hráčů;
`full` má šest kapitol pro 8–30 hráčů. Ve zkrácené hře se použijí příběhové
body 1, 3 a 6: výchozí stopa, propojení zdrojů, poslední akce před hlasováním.
Texty proto neslibují šest kol při zvolení krátké hry. Načtení katalogu ani
obnovení obrazovky nevyvolává generování textu nebo placené AI volání.

### Smrtijedi a bystrozorové: Případ zamčeného letaxu

V bystrozorském úkrytu někdo před prvními tahy změnil ochranné otisky tajné
letaxové trasy. Krb je uzavřený. Bystrozorové potřebují propojit stopy zásahu;
Smrtijedi vědí, kdo do jejich sítě patří, a snaží se předejít dostatečnému
odhalení ve finále. Nejde o povinný souboj: vyšetřování, ochrana, spolupráce,
blufování a dovolená sabotáž jsou skutečné možnosti podle společných pravidel.

| Kapitola | Dění dostupné hráčům |
| --- | --- |
| Krb nepřijímá návštěvy | V archivu se zpřístupní první ochranné záznamy. |
| Všichni mají pověření | Prostor k vyjednávání a práci s dosavadními důkazy. |
| Kdo prošel halou | Přichází druhá rodina stop: přístup k trase. |
| Důvěra z ruky do ruky | Předávání důkazů a ověřování známých souvislostí. |
| Popel si pamatuje | V krbovně jsou dostupné závěrečné otisky. |
| Poslední příkazy | Poslední potvrzené akce, jejich výsledky a teprve potom hlasování. |

Ukázka úvodu: „Úkryt měl znemožnit vstup nepřátelům; jejich odchod zatím
zvládá lépe.“ Pointa vychází přímo z uzavřené trasy a narušení ochrany.
Neříká nic o konkrétním tajném členovi týmu.

### Společenství vlastníků krbu

Obyvatelé domu v Příčné ulici potřebují ráno odjet společným krbem.
Přednostní cestující si však před začátkem schůze přičarovali výjimky
v pořadníku. Krb zjistil rozpor mezi pořadníkem a ochrannou pečetí a spojení
uzavřel. Mimořádná schůze má jediný všední cíl: vyřešit původce změn.
Sázkou není konec světa, nýbrž sousedská výhoda a možnost dostat se do práce.

| Kapitola | Dění dostupné hráčům |
| --- | --- |
| Mimořádně běžný provoz | První změny domovní knihy a uzavřený krb. |
| V zájmu všech sousedů | Vyjednávání, komu věřit a co ověřit. |
| Jen jsem šel kolem | Záznamy příchodů pomohou propojit původní stopy. |
| Malá sousedská výpomoc | Sdílení známých důkazů, ochrana a další dovolené volby. |
| Výjimka pro všechny vybrané | Otisky přednostních výjimek doplní řetězce. |
| Ještě jeden bod programu | Poslední akce a skutečné výsledky před rozhodnutím. |

Ukázka premisy: „Krb problém vyřešil tím, že nepouští nikoho.“ Následná
satira vychází ze sousedských zájmů a důsledků konkrétního pořadníku.
Není to řada náhodných razítek, nadávek nebo univerzálních hlášek o osudu.

V této variantě se krajní akce jmenuje **Proměnit v domovní ozvěnu**.
Neusmrcuje postavu. Úspěšný zásah ji až do konce partie převede do stejného
mechanického stavu `Echo`: žádné tělesné akce ani předávání předmětů, ale
zachovaná práce s dostupnými stopami, předávání známých informací a hlas ve
finále. Tuto podobu následku vysvětlují úvod, stav postavy i potvrzení akce.
Nepřidává se obnova tělesného stavu, druhý krajní žeton ani nová schopnost.

## Společné mechaniky a hranice příběhu

- Počet tajných členů týmu, velikost hlasovacího seznamu a hranici vítězství
  stále určuje původní serverové pravidlo podle původního počtu hráčů.
- Zůstává pět propojených zón, jeden hlavní příkaz na kapitolu, limity pohybu,
  potvrzování plánů, pravidla spánku, ochrany, předmětů a krajního žetonu.
- Rodiny důkazů A/B/C používají stejný ověřený graf a rozpočet informací.
  Samotná stopa ani dvojice příslušnost neprozrazují; odpovídající trojice
  uzavírá řetězec. Text neurčuje nové kandidáty ani neopravuje výsledek enginu.
- Sady schopností se přidělují nezávisle na tajné straně. Jméno sady,
  postavy nebo předmětu nesmí sloužit jako vedlejší kanál pro její odhalení.
- Čtyři kontexty `WORLD_TRUTH`, `EXPERIENCE`, `PRESENTATION` a `COVERT_ACTION`
  zůstávají oddělené. Vyprávěná lež není nová skutečnost a potvrzený záměr
  ještě není úspěšně provedený čin.
- Zpráva kapitoly oznamuje otevřenou možnost nebo dostupný zdroj. Netvrdí,
  že někdo zaútočil, něco předal nebo zemřel, pokud taková událost nevznikla.
- Finálová hláška se vybírá až podle vítěze spočítaného serverem. Osobní
  činy a příčiny ukazuje skutečná rekonstrukce; celé soukromé rozhovory se
  automaticky nezveřejňují. Hůlky se v žádném scénáři neničí.

## Obsahový balíček a přidání dalšího scénáře

Nové varianty jsou v
`backend/Features/SealedCase/Content/scenario-variants.json`. Soubor obsahuje
objekt `variants` s veřejně bezpečnými tematickými popisy. Původní
`scenario.json` dál vlastní společný graf, rozložení a pravidla; nové varianty
je nekopírují. Server sestaví vybranou prezentaci nad stejným enginem.

| Pole balíčku | Účel |
| --- | --- |
| `id`, `version` | Stabilní identita a verze vybraného obsahu. |
| `title`, `summary`, `universe`, `tone` | Katalog, lobby a kontext vypravěče. |
| `guardianLabel`, `conspiratorLabel` | Veřejné názvy stran bez seznamu jejich členů. |
| `guardianObjective`, `conspiratorObjective` | Cíl strany; konkrétní přiřazení hráče se zpřístupňuje odděleně. |
| `publicFacts`, `acceptedPast` | Původní zápletka sdělená před hrou a její připomenutí. |
| `locations` | Popisky existujících zón `VESTIBUL`, `SALON`, `ARCHIV`, `POKOJE`, `KOMORA`. |
| `clueLabels`, `clueDescription` | Srozumitelná podoba rodin A/B/C, bez změny jejich kandidátů. |
| `itemName` | Název společného scénářového předmětu, bez nového oprávnění. |
| `echoLabel`, `echoExplanation` | Zobrazení stavu a přesné zbývající možnosti hráče. |
| `extremeActionLabel`, `extremeConsequence` | Jednoznačný název krajního činu a potvrzení jeho následků. |
| `chapterTitles`, `chapterMessages` | Šest titulů a šest stabilních zpráv; krátká hra použije body 1, 3 a 6. |
| `finaleGuardian`, `finaleConspirator` | Závěr vybraný podle skutečného výsledku. |

Při přidávání scénáře vytvoř nový stabilní identifikátor a vyplň celou
prezentaci. Novou fyziku, počet týmů nebo vítěznou podmínku nelze zavést
změnou názvu či vypravěčova promptu: taková změna vyžaduje samostatné
serverové pravidlo a jeho ověření. Bezpečné rozšíření zachová existující
identifikátory zón a rodin stop, všechny části veřejně pravdivé a poslední
kapitolu použitelnou v obou délkách hry.

Vybraný obsah patří konkrétní partii. Pozdější úprava katalogu nesmí přepsat
již přijatou minulost ani měnit hráčům scénář při obnovení aplikace. Chybějící
volba ve starém požadavku znamená původní Případ posledního zvonu; explicitní
neznámý identifikátor se nesmí potichu změnit na jiný příběh.

Veřejně bezpečný katalog poskytuje `GET /app/v1/sealed-cases/scenarios`
v obálce `{ defaultScenarioId, scenarios }`. Při založení partie se posílá
`scenarioId`; prázdný či neznámý identifikátor vrací
`sealed_case_scenario_unknown` (HTTP 400). Opakování stejné operace s jiným
scénářem není idempotentním opakováním původního požadavku.

V `setup_json` se ukládají `scenarioId`, `scenarioVersion` a
`scenarioSnapshot`. Běžící svět má vlastní kopii prezentace v checkpointu;
jeho záznam verze obsahu odkazuje na identitu vybraného scénáře. Katalog
nepublikuje budoucí kapitoly, přiřazení hráčů ani recept na tajné důkazy.
Herní pohled přidává `scenario`, `ownObjective` a `chapterTitle`; závěr
`finale.message` je dostupný až po odhalení výsledku.

## Vyšetřovací spis a veřejný plán průzkumu

Úpravy po playtestu patnácti her z 29. října 2026 zachovávají pravidla
sabotáže, pomoci a důkazních řetězců. V deníku se vlastní stopy seskupují
podle řetězce; úplnost vyžaduje různé části A, B a C. Teprve úplný řetězec
ukáže průnik kandidátů. Opakované sdílení stejné stopy žádnou část nepřidává.

„Připravit sdílení“ vloží stručné tvrzení do rozepsané zprávy. Hráč před
odesláním kontroluje adresáta a obsah. Sdělení ve formátu odkazu na stopu se
zobrazí zvlášť jako tvrzení autora; ani shodné ID z něj nedělá ověřený doklad.
Přehled rozpozná všechny vložené odkazy i za vlastním textem v jedné zprávě.
Samotný rozhovor nemění vlastnictví stopy. Čítač ukazuje vlastní veřejná i
soukromá sdělení. Deset zpráv bylo omezením testovacího adaptéru, nikoli novým
pravidlem produktu.

Inicializovaný herní pohled přidává `routes` a `sourceSchedule` s veřejným
rozpisem `{ family, label, location, releaseChapter }`, včetně budoucích
kapitol z uloženého scénáře konkrétní hry. Neobsahuje ID ani kandidáty stop,
přesměrování nebo skutečné zdržení doručení. Původní `sources` zůstávají
seznamem zdrojů dostupných v aktuálním místě. Bdělá postava vidí nejkratší
trasu a upozornění, pokud se přesun s pátráním nevejde do zbývajících tahů.
Příprava přesunu vybere pouze první sousední místnost; plán vyžaduje vlastní
výslovné potvrzení.
Rozepsaný typ akce, její ostatní parametry a vlastní text při tom zůstávají
zachované. Bez rozepsané akce se připraví pátrání s přesunem.

`ownOutcomes` rozlišuje vyhodnocený tah od přijetí plánu. Volitelné
`observation` rozlišuje nový nález, opětovné prozkoumání známé stopy a místo
bez nové dostupné stopy, pouze podle uložené vlastní zkušenosti daného tahu.
Neodhaluje interní výsledek střetu ani cizí tajné stavy. Nové potvrzení
sabotáže vyžaduje zdroj a pomoc jinou cílovou postavu; starší uložené příkazy
zůstávají vyhodnotitelné podle původního bezpečného fallbacku.

Lokální syntetický náhled po buildu: `npm run preview:sealed-case --prefix
mobilni-aplikace`. Browser regrese spisu: `npm run test:sealed-dossier:browser
--prefix mobilni-aplikace`; původní průchody: `npm run test:sealed-case:browser
--prefix mobilni-aplikace`. Tyto fixtures neprokazují živé AI, telefon ani
lidské blufování.

## Redakční podklad

Nové texty vycházejí z [Výchozího humoru Wand Night Stand a Kantůrkova
principu](https://chatgpt.com/space/page_a792029311e88191af1554d26af1cfe8),
z [produktového rozhodnutí](../PRODUCT.md#humor-celé-aplikace) a
z [verzovaného redakčního návodu](humor-style.md). Jsou původně české;
nepřebírají Pratchettovy věty, postavy ani konkrétní hlášky. Vypravěč sleduje
všední zájem a jeho absurdní důsledek. Ostrost neurčuje Dobro/Zlo.

Funkční popisky, souhlasy, chyby a následky akcí mají přednost před pointou.
Vtip se nestřídá při načítání a nevzniká z nedoložené osobní vlastnosti hosta.
Úprava tohoto dokumentu sama nemění instrukce běžícího modelu; zdrojem
runtime textů a scénářového kontextu je vybraný serverový balíček.
