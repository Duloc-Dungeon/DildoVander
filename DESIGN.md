# Wand Night Stand — schválený design v2

Zdroj návrhu: `WNS-kompletni-balik-v2.zip`, edice 2.0, 26. 10. 2026.
SHA-256 archivu: `cc4a584aead44719589b54740670b3745095c15d99132b0aead9fd047abc5672`.
Inventura návrhu vychází z `8c202e50ab4a86bf702203a49b79d4943c0e3fc1`.
Implementace začala na `c5a7ffb54121f3aa974cf0fa5b1e18addc37b19f` větve cleanup;
její odstraněné podklady se neobnovují hromadně.

## Vizuální směr

Téměř černé pozadí `#080909`, místní Manrope s českou diakritikou, velké
samostatné objekty a ploché jantarové, mintové a fialové karty. Jedna dominantní
scéna, čitelná hlavní akce; žádný trvalý ambient ani povinné dekorativní částice.
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

## Závazné hranice

Vue/TypeScript/Pinia/Capacitor zůstávají produkční stack. React showroom je pouze
reference. Oprávnění, passkeys, souhlasy, revize, příkazy, historie, Stop a herní
výsledky vlastní původní doména/server, nikoli animace nebo lokální čas.
Hůlky se neničí. Neexistují fyzické ceny. Převzetí památky je operace přístupu.
Rozhraní i generované texty jsou pouze česky; humor je možný kdekoli, ne povinný.

Událost pro efekt smí pocházet jen z autorizované projekce. Zvuk je výchozí
vypnutý; soukromé signály jsou tiché. Ztracený, skrytý nebo opakovaný event se
nesmí přehrát jako nová událost. Změna skóre nesmí čekat na animaci.
`presentationEffects.ts` testuje tuto hranici. `wnsSound.ts` zatím připojuje
pouze explicitní uživatelský výběr; eventový adapter není důkaz serverového streamu.

Pod pečetí je samostatná doména, nikoli nový duelový GameMode. Jediná WORLD_TRUTH
se nesmí poslat klientovi a následně skrýt CSS. EXPERIENCE, PRESENTATION a
COVERT_ACTION jsou perspektivy. Odeslaná věta není provedená akce a nepozorovaný
výsledek zůstává neznámý. FEIGN_SLEEP zachovává bdělou obranu; REST skutečně uspává.

## Evidence a rozpracovanost

Viz `docs/wns-v2-implementation.md`. Úspěšný build ani syntetický browser server
neprokazují živou AI, skutečný multiplayer, nativní export nebo bezpečnost hardwaru.

Samostatná scéna magického kufru, její přirozený GPT Live rozhovor a původ použitých
ilustrací jsou popsané v `docs/magicky-kufr-workshop.md`.
