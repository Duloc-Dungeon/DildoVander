# Svislá referenční hůlka

Jedna placená obrazová generace z 29. října 2026 přes skutečný
`OpenAiKeepsakeProvider`, model z `backend/appsettings.json`: `gpt-image-2.5-flare`.
Použit byl nezměněný produkční `BuildWandImagePrompt`, verze
`personal-wand-art-v3-visual-only-transparent`, se smyšlenými vizuálními daty.
Žádné osobní údaje ani účet hosta nejsou součástí reference.

- `wand-reference.png`: původní nezměněný výsledek, 1024 × 1536 px, svislá
  hůlka špičkou nahoru a rukojetí dolů; PNG má průhlednost. Model ponechal
  měkkou světelnou auru, takže reference není důkazem dokonale čistého výřezu.
- `prompt.txt`: přesný prompt odeslaný produkčním providerem.
- `receipt.json`: model, parametry, vizuální vstup, SHA-256, rozměry,
  počet plně průhledných pixelů a tokenová spotřeba vrácená providerem.
- `.wand-reveal-reference-attempted`: záznam zahájení jediného placeného pokusu.

Náhled používá uložené PNG, nikoli novou generaci:

```powershell
npm run preview:wand-reveal
npm run preview:wand-reveal -- --waiting-room
```

Příkazy spusť z `mobilni-aplikace/` po `npm run build`. Server vypíše lokální
URL. Celé odhalení přehraje načtení stránky nebo otevření dílny z čekárny;
návrat kurzoru ani dotyk dokončenou animaci nespouští.
Čekárnový náhled má tlačítko **Přehrát oponu**: teprve kliknutí načte čekárnu,
posune portrét do záběru a umožní opakovat oponu. Maže pouze příznak zhlédnutí
portrétu na izolovaném lokálním fixture serveru; produkční chování se nemění.

Opt-in eval má samostatný přepínač
`--wand-reveal-reference <novýVýstupníAdresář> <model>` v backendovém harnessu.
Odmítá existující výstup a před placeným voláním ukládá záznam pokusu.
Browserové a unit testy tento přepínač nikdy nevolají.
