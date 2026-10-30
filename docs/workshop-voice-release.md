# Dílna: řeč, průhledná hůlka a přivítání

Textové rozhodování dílny má samostatné `Wands:DialogueModel` s výchozí
hodnotou `gpt-6-luna`. `Wands:Model` zůstává modelem pro návrh hůlky.
Požadavky na dialog používají tarif `fast`; výchozí reasoning je `none`.
Uložená administrátorská nastavení dílny mají přednost před výchozím
`Wands:ReasoningEffort`. Při nasazení se proto ověřuje i jejich skutečná hodnota.
Model hlasového transportu se tím nemění.
Podporu `none` i Fast pro Luna potvrzuje
[oficiální dokumentace modelu](https://developers.openai.com/api/docs/models/gpt-6-luna).

Live transport čeká na potvrzení delegace 300 ms místo 800 ms; záložní
delegace čeká na klid přepisu 600 ms místo 1200 ms. Nejde o naměřenou celkovou
latenci: odezvu stále ovlivňuje síť, rozpoznání řeči a poskytovatel.
Po uložení hůlky se ztlumí vstup mikrofonu, přehrávání zůstane aktivní.
Opožděné rozpoznávání již nemá přerušovat závěrečný verdikt. Výslovné zastavení
řeči a odchod z obrazovky zůstávají funkční. Samostatný posuvník hlasitosti
byl z dílny odstraněn.
Opožděná změna hlasového tahu může vyžadovat zopakování celé uložené závěrečné
věty pro aktuální tah poskytovatele. Obnova je omezená na osm pokusů a životnost
spojení; ruční Stop další obnovu zakáže. Stav „listening“ z WebRTC se nepoužívá
jako důkaz přehrání posledního vzorku ani k automatickému zavření výstupu.

Generování obrázku hůlky již používalo transparentní PNG. Kontrola však
přijímala i jediný průhledný pixel. Nově vyžaduje průhledné rohy, alespoň 90 %
průhledného obvodu a alespoň 5 % průhledných pixelů. Prompt v5 zakazuje mlhu,
světelnou auru a pozadí. Pixely hůlky se mechanicky nevymazávají; mlhu uvnitř
jinak čistého výřezu nelze spolehlivě odlišit od průsvitného materiálu.
Starší uložené obrázky se bez dalšího zadání hromadně nepřegenerují.

Účet `/me/account` nově zobrazuje uložený osobní text bez načítání portrétu.
Stránka postavy jej ukazuje před obrázkem i během přípravy portrétu a využívá
stejný výběr variant přivítání jako čekárna. Prázdný objekt přivítání již
neskrývá starší uložený závěrečný text. Není-li osobní text dostupný, zůstane
viditelná obecná hláška; není vydávána za personalizovaný výsledek.
Limit generovaného JSON přivítání je 900 výstupních tokenů, aby vedle českých
textů zbylo místo na varianty vtipu a identifikátory podkladů. Limity viditelných
textů se nemění.

Regrese pokrývají cílené testy nastavení a hlasových transportů, obrázků hůlek,
portrétů a komponent účtu. Browser scénáře `character.browser.mjs` a
`wand-workshop.browser.mjs` kontrolují účet během přípravy a dílnu při šířkách
320, 390 a 1280 px. Povinná nativní verze se zvyšuje až po úspěšném vydání
a ověření dostupnosti příslušného podepsaného balíčku.
