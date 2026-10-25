---
name: Wand Night Stand
description: An intimate magical salon for the invitation, countdown, and keepsake experience.
colors:
  primary: "#e0bc7e"
  on-primary: "#211a22"
  background: "#211a22"
  surface: "#2b222d"
  surface-raised: "#382d39"
  foreground: "#f4ecd9"
  muted: "#c4b8c1"
  accent-soft: "#483a37"
  link: "#edcc96"
  border: "#89747e"
  border-subtle: "#55434f"
  success: "#a9d6b4"
  warning: "#f4c58e"
  danger: "#ffb7b4"
  info: "#cac5ef"
  mark-background: "#251d26"
  velvet-background: "#302327"
  velvet-surface: "#3a2b30"
  velvet-raised: "#48363b"
  velvet-accent-soft: "#57403b"
  velvet-border-subtle: "#69505a"
typography:
  display:
    fontFamily: "Wand Editorial, Georgia, serif"
    fontSize: "clamp(2.75rem, 5.8vw, 5.75rem)"
    fontWeight: 500
    lineHeight: 0.98
    letterSpacing: "-0.025em"
  body:
    fontFamily: "Inter, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif"
    fontSize: "0.9375rem"
    fontWeight: 400
    lineHeight: 1.55
rounded:
  control: "8px"
  artwork: "12px"
  card: "14px"
  mark: "16px"
spacing:
  sm: "8px"
  md: "16px"
  lg: "24px"
  xl: "32px"
  2xl: "48px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.on-primary}"
    rounded: "{rounded.control}"
    padding: "0.85rem 1.25rem"
    height: "48px"
  field:
    backgroundColor: "{colors.background}"
    textColor: "{colors.foreground}"
    rounded: "{rounded.control}"
    padding: "8px 12px"
    height: "44px"
---

## Overview

Wand Night Stand is an intimate magical salon: editorial, tactile, quietly theatrical, and lightly witty. The consumer-facing interface pairs an original geometric W-and-crescent mark with curtain and night-flower artwork. Use the self-hosted display face for expressive headings and the established sans-serif UI face for readable controls and supporting copy.

**The Consumer Boundary Rule.** This document governs the consumer identity in the application bundle. The separately maintained shared design system, simulator, offline CAD viewer, and engineering surfaces retain Vapour Chrome. Technical names, firmware, protocols, and device behavior are outside this visual identity.

The consumer token overrides live in [consumer-brand.css](mobilni-applikace/src/application/consumer-brand.css), the launch composition and responsive rules in [LaunchView.vue](mobilni-applikace/src/features/launch/LaunchView.vue), and approved artwork and font provenance in the [brand asset manifest](mobilni-applikace/src/assets/brand/manifest.json).

### Engineering surfaces: Vapour Chrome

Engineering viewers, the simulator, offline CAD, and shared design-system sources continue to use Vapour Chrome, as documented in the [shared design-system guide](design-system/README.md). Use Vapour Mist (`#EEF1F5`), Chrome Midnight (`#1B2430`), Steel Grey (`#8A94A6`), and Digital Periwinkle (`#B8C0FF`) through semantic tokens. Inter serves UI and headings; IBM Plex Mono serves data. Preserve calm surfaces, useful whitespace, clear active and focus states, responsive task-oriented navigation, and physical-material artwork. The `aw-*` namespace and `arcane-workshop.css` filename remain compatibility interfaces.

Operating tools prioritize state, control, and recovery; the CAD viewer prioritizes the model; public pages prioritize the visitor's keepsake and story. Shared visual principles do not require identical density. Theme preferences belong to each consumer; public private-data rules take precedence over persistent preferences.

## Colors

Ink is the page ground, with raised plum surfaces grouping forms and controls. Ivory carries primary text; muted mauve carries supporting copy. Brass is reserved for the primary action and small moments of emphasis. Links use a lighter brass tint, while quiet plum borders separate content without turning each region into a card.

**The Velvet Evening Rule.** The persisted `olivander` theme key applies the warmer velvet surface and border variant. Keep that key stable; translate theme names only in the interface. The mark uses its own slightly lighter ground from `mark-background`.

Use the semantic success, warning, danger, and info colors for their matching states. Do not use brass as the only indication of status.

## Typography

`Wand Editorial` is the self-hosted Cormorant Garamond 500 display face, loaded with `font-display: swap`; use it for the wordmark and major page headings. Inter remains the UI face, loaded from the shared design-system font assets, and serves body text, controls, and the tabular countdown numerals. Georgia is the display fallback; the UI stack falls back through platform sans-serif faces.

The launch heading uses a fluid display scale; identity headings use a slightly narrower fluid scale. Keep compact labels and field instructions in the UI face. Avoid forcing display lettering into long paragraphs or dense data.

**The Two Voices Rule.** Display lettering supplies the salon's character; Inter keeps actions, labels, and countdown values easy to scan.

## Layout

The launch view fills at least the dynamic viewport height, uses safe-area padding, and caps its content width. The launch-mode shell also supplies a centered responsive outer gutter to route content (from 1.25rem to 3rem). Wide screens place the portrait artwork and launch copy in two columns. At 760px and below the view stacks into one column, changes the artwork crop, and places footer content vertically. At 420px and below, masthead support navigation is hidden while the language control remains available. At 1600px and above, the top inset increases.

**The Curtain First Rule.** The curtain image is the prelaunch and invitation artwork; the keepsake still life belongs to postlaunch home and archive empty states. Keep the subject's crop responsive and do not expose future-feature imagery before launch.

Forms use clear labels, grouped controls, and generous touch targets. Keep help links separated when wrapping and preserve room for keyboard focus indicators. The four-part countdown uses tabular numerals and stable columns.

## Elevation & Depth

Depth comes from the original curtain and flower artwork, layered plum surfaces, and restrained one-pixel separators. The portrait reveal uses a brief opacity fade; reduced-motion preferences remove that transition. Avoid adding generic shadows or decorative effects where the existing surface contrast is sufficient.

## Shapes

Controls have gently rounded corners, content cards are slightly softer, and art has a modest crop radius. The W-and-crescent mark keeps its own rounded square field. Use these distinct roles rather than making every surface a pill or a nested card.

## Components

Primary actions use brass fill, ink text, a clear minimum height, and a comfortable horizontal inset. Secondary controls remain outlined or transparent against the plum surface. Text inputs use the page ground, a visible border, ivory text, and a minimum touch height; focus-visible outlines use the info color with a clear offset.

## Do's and Don'ts

- Do use the original mark, curtain crop, and approved postlaunch still life in their intended states.
- Do keep the Czech/English interface chrome localized and retain readable contrast, visible keyboard focus, and reduced-motion behavior.
- Do preserve administrator-authored teaser and host-note wording as configured. The server payload has no language field, so the interface labels those strings as original-language notes; do not automatically translate them when the interface language changes.
- Do keep the persona's voice and operational content distinct from the visual brand; visual rebranding does not change its safety, language, or device-control rules.
- Don't show suitcase imagery or future keepsake artwork on the prelaunch surface.
- Don't rename persisted theme identifiers, engineering namespaces, device names, protocol labels, or firmware behavior as part of consumer visual work.
- Don't apply the consumer palette to the shared engineering design system, simulator, or offline CAD viewer.
