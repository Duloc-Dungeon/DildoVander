# AI hráči v lobby Pod pečetí

Owner, který případ založil a patří do jeho aktivní herní skupiny, může před
uzavřením obsazení přidat AI hráče po zvoleném počtu nebo doplnit zbývající místa.
Zákaznická role k vedení lobby není nutná. Bez ní zůstává owner pozorovatelem:
nemá vlastní hráčský tah, hlas ani soukromý hráčský kontext.

AI místa se počítají do původní kapacity 8–30 hráčů a pravidel N/T/K. Po naplnění
se zmrazí společně s lidským obsazením; po startu už je přidat nelze. Mohou tvořit
i celé obsazení. U portrétové řady mají viditelné označení AI. Nevznikají uživatelské
účty, přihlašovací údaje, lidské profily ani nově generované portréty.

## Nastavení

Vedle existujícího zapnutí identity a `SealedCase:Enabled` nastaví provozovatel:

| Konfigurace backendu | Význam |
| --- | --- |
| `SealedCase:AiPlayers:Enabled` | Výchozí `false`. Zapne provider a aplikační background worker při startu backendu. |
| `OPENAI_API_KEY` | Existující serverový klíč; nikdy se neposílá do klienta ani nezapisuje do zdrojů. |
| `SealedCase:AiPlayers:Model` | Výchozí `gpt-5-mini`. |

Pro proměnné prostředí .NET odpovídají názvy
`SealedCase__AiPlayers__Enabled` a `SealedCase__AiPlayers__Model`.
Po změně nastavení je nutné restartovat backend. Bez zapnutí nebo klíče server
odmítne přidání nových AI míst; lobby se nezaplní neaktivními zástupci.
Tato implementace nemění produkční nastavení ani nespouští deployment.

## Chování a soukromí

- Každá AI se rozhoduje podle svého oprávněného pohledu a nabídnutých legálních
  voleb. Hlasy vybírá podle vlastních získaných stop a tajného týmu.
- Poskytovatel dostává strukturovaná fakta a pseudonymy míst u stolu. Lidská jména,
  profily, ID účtů, volné poznámky a lidské rozhovory se do API neposílají.
  AI tedy nevyhodnocuje lidské volné zprávy; může psát vlastní veřejná tvrzení.
- Přidání samo nevolá placené API. Rozhodování začne až v běžící partii při
  plánování nebo hlasování. Dva souběžné požadavky omezují zatížení.
- Jeden AI hráč má nejvýše jedno placené rozhodnutí pro každou kapitolu a každé
  hlasovací kolo: nejvýše osm za plnou partii. Formátová chyba, timeout nebo
  nedoložený výsledek po restartu použijí vyčkání či abstenci bez dalšího placeného
  pokusu. Uložené rozhodnutí čekající během pauzy se po obnovení znovu negeneruje.
- Stav rozhodnutí a identifikátor potvrzení jsou trvalé. Souběžný nebo obnovený
  runner provádí skutečně uložené kanonické rozhodnutí; retry nepřidá tah ani hlas.
- Ztráta role ownera, členství nebo privacy fence zastaví další práci. Výmaz
  dotčeného případu odstraní také AI místa a uložená rozhodnutí. Dokončené partie
  se přestanou plánovat.

## API a ověření

`POST /app/v1/sealed-cases/{id}/ai-players` přijímá `operationId`,
`expectedRevision` lobby a celočíselný `count`. Oprávnění ownera/zakladatele,
aktivní skupina a privacy se ověřují i před opakovaným potvrzením. Klient opakuje
stejný požadavek po nejisté síťové chybě; po jednoznačném odmítnutí porovná aktuální
obsazení a připraví nový požadavek. Nativní transporty povolují tuto přesnou cestu
pouze pro POST. Hráčské endpointy si ponechávají zákaznické oprávnění.

Nově provedené lokální testy (offline provider, žádné nové placené partie):

- Release build a souhrnný `--sealed-case-runtime`: PASS, včetně původních
  privacy/restart/regresí a skutečných HTTPS her pro 8/30 lidských klientů.
- `--sealed-case-ai-lobby`: PASS, owner i bez zákaznické role, budoucí odpočet,
  disabled/missing-key, kapacita/freeze, ztracené potvrzení a odebraná role.
  Dokončený tutorial s ownerem a sedmi AI, třicet AI míst, restartový souběh,
  přesná uzávěrka, pause/resume, pseudonymizace a výmaz obou AI tabulek.
- Frontend build a browser 320/390 px: PASS, ovládání pouze pro ownera, doplnění
  míst, lost-ack/reload bez duplikace, nový požadavek po 409 a pozorovatel bez
  hráčského ovládání.
- Nativní allowlist regrese jsou rozšířené; nové Swift/Android změny nebyly v tomto
  kroku lokálně sestaveny. Fyzický telefon a lidský pilot nejsou tímto ověřené.

Evidence je v ignorovaném `backend-tests/bin/pod-peceti-completion/ai-lobby-*`.
Počet dříve povolených placených testovacích partií zůstává 4/10; uvedené nové
simulace používají testovací adaptér a do tohoto počtu nepřidávají API volání.
