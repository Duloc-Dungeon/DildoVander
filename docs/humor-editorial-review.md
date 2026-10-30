# Redakční výběr českého humoru

Lokální úprava 30. října 2026. Kandidáti jsou původní návrhy pro tuto aplikaci;
nejde o záznam placeného modelového vyhodnocení. Výběr posuzuje příčinu pointy,
čitelnost, rytmus, soulad se stavem a odlišnost od okolních hlášek.

| Místo | Varianta A | Varianta B | Vybraná varianta C a důvod |
| --- | --- | --- | --- |
| Portrét bez osobních podkladů | Zrcadlo má svůj názor. | Skromnost se do rámu nevešla. | „Portrét: způsob, jak udělat dojem a ani se přitom nepohnout.“ Falešná definice; A nemá důsledek, B přisuzuje neověřenou vlastnost. |
| Povýšení hůlky | Tohle dřevo už ti nestačí. | Hůlka stoupá po kariérním žebříčku. | „Hůlka povyšuje. Na vedoucí dřevo.“ Antiklimax, krátký konkrétní obrat namísto nedokončené metafory. |
| Úder proti štítu | Štít se urazil. | Stížnost do zavřené podatelny. | „Úder šel přímo k věci. Tou věcí byl štít.“ Doslovný obrat vychází ze skutečné dvojice tahů; nepotřebuje přidaný úřad. |
| Remíza | Dva mágové. Jedna nerozhodnost. | Sláva se dnes rozdává napůl. | „Remíza: vítězství, na kterém se nikdo neshodl.“ Falešná definice vedle doslovného výsledku; neobviňuje hráče z nerozhodnosti. |
| Historie soubojů | Archiv bojuje s prázdnou stránkou. | Paměť má vlastní pravidla. | „Paměť časem přikrášluje. Skóre nemá představivost.“ Kontrast přesného záznamu a vzpomínání, použitelný i u naplněné historie. |
| Kronika před prvním zápasem | Kronikář čeká, komu musí oslavu připsat. | Kronikář má předčasné sebevědomí. | „Kronikář má připravený slavný úvod. Chybí mu už jen důvod.“ Úspornější obrat a český rytmus; B jen pojmenovává vlastnost. |

## Pokrytí a hranice

Pevné texty: představení postavy, žebříček, postup hůlky, kronika, výsledky
soubojů, památky, Pod pečetí a komentáře odehraných kol. Funkční texty,
chyby, souhlasy a bezpečnostní pokyny zůstávají doslovné.

Generátory: uvítání postavy, dílna, živý hlas, party persona, kronika
soubojů, Baron, scénáře a vyhodnocení YOLO, příběh hůlky a vypravěčské role
Pod pečetí. Společné instrukce vybírají jeden vhodný mechanismus, vyžadují
porovnání možností a zakazují zveřejnění variant místo smluveného výstupu.
Nezavádějí další modelová volání. Rozhodčí, orchestrátor a návrhy akcí
Pod pečetí tento kreativní dodatek nedostávají.

Existující uložené odpovědi se nepřepisují. Kvalita nově generovaného textu
je stále závislá na modelu a kontextu; testy zapojení promptu nejsou důkazem
kvality živého generování.


## Ověření a provozní hranice

- Frontend: 70 cílených testů (LaunchView, GameRanking, CustomerDuelHistory,
  CustomerSealedCaseRewards), build včetně kontroly generovaných kontraktů a typů.
- Backend: sestavení testovacího projektu bez chyb; offline skupiny mobile-game,
  duel, character-portraits, mobile-game-boss, generated-wands a sealed-case-http.
- Prohlížeč: syntetický žebříček a povýšení hůlky na localhostu; titulek povýšení
  ověřen také při 390 × 844. Nejde o kontrolu každé obrazovky či nativního telefonu.
- Existující vlastní prompty uložené administrátorem mohou přepsat výchozí prompt
  dílny. Jejich databáze ani produkční konfigurace tato zdrojová úprava nemění.
- Žádné placené generování, publikace, commit ani nasazení. Výstupy uložené dříve
  zůstávají historickým obsahem. Kontrolu změn provedl autor bez další delegace
  podle výslovného přání uživatele.
- Následně prošla i skupina sealed-case-playable: šest rolí AI, oddělení veřejného a soukromého kontextu a úplné syntetické hry pro 8 i 30 klientů.

## Rozšíření variant a obrázků

Výběr nyní zahrnuje 45 hotových statických hlášek v 15 sadách a 3–6
uložených osobních alternativ pro nové uvítání. Varianta s pouhou abstraktní
vlastností byla nahrazena konkrétním důsledkem (rám brání dojmu v útěku,
datum slavného dne se píše tužkou). Varianty rozlišují antiklimax, doslovnost,
falešnou definici a ironické vysvětlení výsledku; nepřidávají herní fakta.
Portrét a ilustrace kroniky mají zadání jedné situační obrazové pointy.
Vizuální podoba hůlek zůstává věrná. Nové obrázky nebyly placeně generovány;
kontrola se týká zapojení promptů, nikoli kvality dosud nevzniklých obrázků.

Doplňkové ověření: 82 cílených frontendových testů; kontrola generovaného
kontraktu; backendové skupiny `character-portraits` a `prelaunch-http` včetně
přenosu variant přes poskytovatele, trvalého uložení a veřejného API. Build
frontendu prošel s existujícími upozorněními na velikost balíků a import
servoBench. V prohlížeči na syntetickém žebříčku se po odchodu a návratu
hláška změnila z „Vedeš. Soupeři tomu říkají průběžný stav.“ na „Jsi v čele.
Zatím.“ Stabilitu při pollingu a změně query ověřuje komponentový test.

## Následná diagnostika portrétu a CI

Po výslovném schválení placených testů byl Harry Potter / Harry Potter odeslán
přes skutečný aplikační provider. Flare vrátil `moderation_blocked` i pro stručné
zadání oblečené knižní postavy. Úspěšný generovaný Harryho portrét tedy není
doložen. Identita se nemaskuje a další automatické placené pokusy se nezavádějí.
Obrázkové zadání je oddělené od textových instrukcí, zachovává jméno, svět a
rozpoznávací znaky. Aplikace rozlišuje odmítnutí od obecné chyby HTTP 400 a
zachovává dohledání licencovaného obrázku se zdrojem. Testy ověřují skutečné
napojení transportu, jediný požadavek, omezení čtení chyb a ochranu údajů.

Celá frontendová sada: 160 Node testů a 1620 komponentových testů. První
souběžný běh měl jeden timeout screenAccess; samostatný celý běh prošel
bez změny testu. Build prošel. Syntaxi workflow ověřil actionlint 1.7.12;
151 Node CI testů a 159 Python CI testů prošlo (27 lokálních omezení/skipů).
Nové testy agregované development brány ověřují neúspěch, zrušení a přeskočení
vybraného projektu i chybějící metadata.

Sedm site/browser sad následně prošlo lokálně, stejně jako prelaunch HTTPS
a bounded SDK transport. Prohlížečový test uvítání nyní přijímá přesně jednu
z trojice schválených fallbacků místo odstraněné jediné hlášky o zrcadle.
