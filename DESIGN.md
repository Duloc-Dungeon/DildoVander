---
name: Wand Night Stand — Responsive Table
description: Clean mobile-first character, invitation and duel experience.
colors:
  primary: "#d9ad67"
  on-primary: "#10251d"
  background: "#041b18"
  surface: "#0c2823"
  surface-raised: "#183b32"
  foreground: "#f5e7ca"
  muted: "#c8cdbb"
  accent-soft: "#253c2e"
  link: "#f1cf94"
  border: "#7e8972"
  border-subtle: "#365046"
typography:
  display:
    fontFamily: "Inter, system-ui, sans-serif"
    fontSize: "clamp(1.9rem, 4vw, 3.25rem)"
    fontWeight: 600
  body:
    fontFamily: "Inter, system-ui, sans-serif"
    fontSize: "1rem"
    lineHeight: 1.55
---

# Vzhled aplikace

[Závazný profil produktu](PRODUCT.md) určuje aktuální zaměření, češtinu,
humor a přednost pravidel. Vizuální předlohou celé mobilní zkušenosti je
[The Responsive Table](design-system/reference/responsive-table.png),
Preview 2, schválený vlastníkem 26. října 2026.

Tmavě zelený podklad, slonová kost, mosazné akce a čistá bezpatková typografie.
Používej lokální Inter, sémantické tokeny, skutečná data a společné Vue prvky.
Velikost a rozestupy vytvářejí hierarchii; omezený počet karet odděluje úkoly.
Primární akce má hmatově působící mosazný povrch, vedlejší zůstává klidná.
Animace potvrzují výběr a výsledek. Nezdržují akci a mají statickou alternativu.

Runtime je [consumer-brand.css](mobilni-aplikace/src/application/consumer-brand.css).
Herní skladbu a aktiva popisuje [mapa spotřebitelského UI](design-system/consumer-game-ui.md).
Kompatibilní identifikátory `lord-voldemort` a `olivander` zůstávají; obě témata
patří ke stejnému zelenému vizuálnímu směru. Původní patkové fonty a obrazové
materiály nejsou výchozím stylem nových obrazovek.

## Mobil a přístupnost

- Primární zákaznické cesty: postava, hra, památky a účet; hostitel má večer
  a pozvánky. Hardwarové nástroje se v běžné navigaci nenabízejí.
- Dotykové cíle nejméně 44 × 44 px, jasný focus, čitelný kontrast, safe areas,
  320px šířka a 200% text bez vodorovného přetékání celé stránky.
- Jedna hlavní akce v logické skupině. Funkční stavy jsou doslovné, vtip zvlášť.
- Žetony a stůl jsou dekorace; neověřené skóre, podoba nebo průběh nejsou dekorace.
- Výběr, čekání a výsledek rozlišuje i text. Omezený pohyb vypíná animace.
- Opona má vlastní dvě křídla a pevný rám; její existující stavový kontrakt platí.

## Hranice

Tento vzhled se aplikuje pouze v mobilní/webové aplikaci. Samostatný simulátor,
CAD a sdílené engineering výstupy zachovávají [Vapour Chrome](design-system/README.md).
Názvy `aw-*`, témata, zařízení a protokoly jsou kompatibilní API.
Vizuální změna nemění oprávnění, souhlas, serverové rozhodnutí ani fyzický stav.
