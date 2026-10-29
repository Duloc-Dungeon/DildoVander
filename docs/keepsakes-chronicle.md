# Osobní ilustrovaná kronika

Mobilní Památky spojují potvrzené herní události, jejich neměnný příběh,
situační kartu a osobní odznaky. Původní archiv vydaných hůlek zůstává dostupný.
Nevzniká veřejný webový portál. Sdílení používá systémový dialog telefonu.

## Zdroje a pravdivost

- Každý dokončený mobilní duel a YOLO má záznam. YOLO se ilustruje vždy;
  běžný duel při milníku nebo na výslovné „Vytvořit ilustraci“.
- Týmový zápas zachovává skutečné účastníky, týmy a výsledek. Boss rozlišuje
  osobní pokus/výhru a společnou příběhovou událost.
- Výběr dokončené hůlky zakládá památku. Dokončený upgrade ukládá i popis
  předchozí hůlky a získání „Druhého vydání“. Samotné odemčení upgradu nestačí.
- Pod pečetí vydává památku až po `released` a dostupné rekonstrukci.
  Používá uvolněné výsledky a skutečné akce; soukromé zprávy, plánovací
  poznámky, hlasování a tajné podklady se nekopírují do obrazového zadání.
- Správce může jednou uložit závěr otevřeného večera. Každý člen získá
  vlastní výběr již odhalených momentů. Tento bod uzavírá výběr vzpomínek;
  nemění dostupnost dalších her. Opožděné odhalení nepřepisuje uložený závěr.
- U dokončeného rozhovoru v dílně lze výslovně uložit jednu vybranou repliku
  vypravěče. Celý rozhovor se nekopíruje. Tato památka je osobní a nesdílitelná.

Odznak má vlastníka, rozsah, potvrzený čas získání, konkrétní důkaz a odkaz na
zdrojovou památku. Tři původní tituly zachovávají skupinovou působnost.
Nové osobní milníky používají účet; rivalita a společné fiasko dvojici/skupinu.
Odznaky jsou kosmetické a nemění XP.

Zpětné doplnění vychází z uloženého dokončení a skutečných výměn. Chybějící
historický portrét ani rozhodující volba se nevymýšlejí; reference zůstává
označená jako neznámá. Nové hry zmrazují profil, podobu hůlky, verzi podkladů
a dostupnou kopii portrétu při zahájení. Pozdější změna postavy je nepřekreslí.

## Ukládání a generování

`Features/Chronicle` vlastní `chronicle.sqlite` pod `ApplicationIdentity:DataRoot`.
Autoritativní výsledky nadále vlastní jejich původní úložiště. Dokončení zapíše
obálku události do outboxu ve stejné SQLite transakci jako výsledek. Pracovník
obálku doručí; unikátní `(sourceKind, sourceId)` zaručuje stejnou památku i při
opakovaném doručení nebo restartu. Čtení galerie generování nespouští.

Tři nezávislé okamžiky jsou potvrzení události, dokončení ilustrace a odhalení
konkrétním vlastníkem. Před odhalením API vrací pouze neutrální zástupný záznam.
YOLO přizná odznaky a kartu až po odhalení verdiktu; výslovné otevření starší
památky také potvrzuje odhalení. Obrázky jsou autorizované stejně jako detaily.

Generování má trvalou frontu a oplocené pronájmy. Stav `dispatched` se uloží
před zavoláním poskytovatele. Neznámý výsledek nebo restart během volání přejde
do `awaiting_reconciliation`; automatické opakování placeného volání nenastane.
Výslovné opakování v aplikaci může vytvořit novou ilustraci, nikdy nový výsledek
již skončené hry. Čitelná karta a odznaky zůstávají i při chybě poskytovatele.

`Chronicle:ProviderMode` přebírá při absenci `Keepsake:ProviderMode`.
Hodnota `openai` používá stávající nakonfigurovaný klíč a
`Chronicle:ImageModel` (nebo `Keepsake:ImageModel`). Klíče se do kroniky ani
klientů neukládají. `fake` je výslovná lokální testovací cesta; karta se viditelně
označí jako zkušební. Při chybějící konfiguraci se nezobrazuje smyšlená ilustrace.

Text a grafika používají stejné zmrazené události. Model kreslí scénu bez
nápisů; aplikace doplní přesná jména, výsledek a datum. Odznak přebírá vnitřní
obrazový motiv z vygenerované scény a dostává společný rám a symbol achievementu.
Nevytváří další nezávislou verzi historie.

## Soukromí a API

[HTTP kontrakt](../contracts/chronicle-openapi-v1.json) popisuje seznam,
filtrování, detail, odhalení, obrazové úlohy, obrázky odznaků a export metadat.
Účet má přístup ke svým historickým skupinám; aktivní herní skupina není
podmínkou. Raw vizuální snapshoty a portrétové bajty nejsou součástí klientského
DTO. Soukromé vzpomínky nelze sdílet. Sdílené karty používají jen povolenou
projekci uloženého příběhu, ne aktuální profil nebo soukromý rozhovor.

Smazání účtu nejprve zablokuje nové doručení. Odstraní celé sdílené scény
obsahující daného účastníka, jejich obrázky, odznaky, úlohy i zdrojové kopie.
Pozdní výsledek generování ani starý outbox záznam účet neobnoví. Samostatný
export kroniky obsahuje odhalená metadata; existující omezený v1 export
osobních dat se touto funkcí nerozšiřuje o příběhy ani média.

## Lokální ověření

Před každým odesláním sdílené odpovědi a přijetím obrazové úlohy se kontrolují
aktuální identity a profily všech zobrazených účastníků. Přijatá žádost o smazání
proto zablokuje cizí kopie i před dokončením mazání na pozadí. Smazání jedné
postavy odstraní jen její staré kopie a neblokuje novou postavu stejného účtu.

Zpětné doplnění starších mobilních zápasů běží v omezených dávkách. Záznam bez
ověřitelného času nebo s nečitelnými daty dostane trvalou značku přeskočení;
nevymyslí si datum a neblokuje následující platné události.

```powershell
dotnet run --project backend-tests/BackendTests.csproj -- --chronicle
dotnet run --project backend-tests/BackendTests.csproj -- --chronicle-http
dotnet run --project backend-tests/BackendTests.csproj -- --mobile-game
dotnet run --project backend-tests/BackendTests.csproj -- --sealed-case-playable
npm test --prefix contracts
npm run typecheck --prefix mobilni-aplikace
npm run preview:chronicle --prefix mobilni-aplikace
```

Náhled kroniky používá syntetická data, nenavazuje produkční přihlášení a nevolá
poskytovatele. Webový náhled a testy zásad nativního transportu nedokazují běh
sdílecího dialogu na fyzickém iPhonu či Androidu. Ten patří do nativní akceptace.
