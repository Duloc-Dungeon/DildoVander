# Game character artwork

Approved synthetic character expression sheets: Bohumil Křivopísk and Baron
Šarlatán, each with three expressions from left to right. These are fictional
characters, never guest identities. Original PNG sources remain editable here.

Run `python scripts/prepare_game_assets.py` with Pillow available to export the
bounded WebP sheets and their source/runtime hash manifest under
`mobilni-aplikace/src/assets/brand/game/`. `GameCharacter.vue` selects a third of
each sheet with CSS; visible status text conveys the same meaning. No text is
embedded in the artwork. Runtime export performs resizing/compression only.
