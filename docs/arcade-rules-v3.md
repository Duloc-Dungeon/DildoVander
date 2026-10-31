# Kouzlo bez dozoru: Noční inventura (`spark-v3`)

V3 combines learnable three-gate patterns, moving shelves and player-created
echoes. V1 and V2 tickets, replays and records retain their original rules.
The client requests V3 explicitly; omission continues to mean V1. V3 bests
use a separate additive table. Pending older submissions remain retryable.

## Integer simulation contract

Keep the V2 field, 60 Hz clock, movement, scroll, limits and input format.
Gate zero stays at distance 120, center 244, width 168 and height 22.
Subsequent gates stay 180 pixels apart. For index `i > 0`, block and slot are
`floor((i-1)/12)` and `(i-1)%12`. Advance unsigned xorshift32 once at slot zero
(zero seed becomes one). First three blocks select pattern `block%3`; later
blocks select `random%3`. Mirror from index 35 when `(random&1)==1`.

Slots 0–2 use offsets `[-48,0,48]` (stairs), `[-48,48,-48]` (S), or
`[0,48,0]` (sleeve), relative to 180. S slot 2 and sleeve slot 0 are 72 high;
other gates are 22 high. Width is `max(104,164-floor(i/12)*4)` for ordinary
gates, `max(132,172-floor(i/12)*4)` for long gates. From index 60, slot 3
continues the mirrored pattern at offset -48. Slots 9–11 are recovery gates
at center 180, width at least 160.

From index 15 slot 3 moves; from 35 slot 4 moves with it. From 60 odd blocks
use opposite motion for slot 4. From 90 slots 0–2 also move together and slot
2 is always long. Moving gates are at least 156 wide before 35, then 140
(long: 156). Amplitude is 12 before 35, 18 before 90, then 24.
Period is 192 ticks. The common anchor is slot 3's distance. Phase is
`48-floor(anchorDistance*5/11)`, normalized modulo 192. With
`u=(tick+phase)%192`, offset is
`sign*(-amplitude+floor(min(u,192-u)*2*amplitude/96))`.
The first single mover crosses its midpoint during contact, with no reversal.
Rendering and collisions use this exact integer center; reduced motion only
removes decorative animation, never physical motion or warnings.

From index 20 slot 5 records an echo; from 100 slot 6 records another.
Sources have center 180 and width at least 148. When scroll first reaches
the source distance, after collision checks, capture the player's exact x.
The target is source index +2. Its gate is static, centered at 180, 264 wide
and 22 high. An announced seal stays at the recorded x forever; half-width
is 8 before source index 40 and 12 thereafter, with the target gate's height.
It is lethal only during geometric contact at the target. Sources and targets
carry distinct shapes; announced seals render immediately two gates ahead.
An imprint outside a useful route is never moved to penalize the player.

Each tick: apply taps, increment tick, move, test walls and every vertically
overlapping gate and seal, then capture source crossings, award fully cleared
gates and discard cleared echoes. Stop at collision or 108000 ticks. Collision
uses circle versus rectangle including corners and touching boundaries.
Course data is immutable; echoes belong to each run, never the shared course.

## Acceptance and tuning

No gate closes. Geometry stays inside the side walls. Three-gate decisions and
two-gate echoes fit in the 480-pixel lookahead. The progression first introduces
wide shapes, then motion, echoes, paired motion, opposing pairs and moving long
passages; the final three slots provide recovery. Pattern families and mirrors
vary between blocks while keeping bounded transitions. Higher tiers combine
known requirements rather than increasing speed indefinitely.

Reachability validation must carry position, direction and active echo imprints
through connected blocks, including complete long-passage contact intervals.
Shared generated witnesses exercise the TypeScript and independent C# verifier;
old fixtures remain unchanged. Automated witnesses demonstrate feasibility for
tested courses, not playtest evidence or a universal human-difficulty guarantee.
Release requires playtesting introduction clarity and late-game combinations.
Ship the compatible backend before the V3 client. No deployment is part of
this source change.

## Reproducible checks

- `node mobilni-aplikace/scripts/generate-spark-vectors.mjs` generates only V3
  fixtures through the actual simulator. The bounded witness search retains
  direction and both active imprints and changes input no faster than 200 ms.
- `npm run test:components --prefix mobilni-aplikace -- src/features/arcade`
  checks replay witnesses, progression, bounds, all relevant collisions, exact
  echo capture, old pending submissions, and connected-course feasibility.
  Imprint checks cover every safe integer x and either outgoing direction at
  source gates 30, 42 and 102, including the second echo generated after 102.
- The backend mobile-game harness consumes all three versions' fixture files
  and checks stored-ticket dispatch, retry behavior, isolated bests and erasure.
- `npm run test:spark:browser --prefix mobilni-aplikace` exercises the built app
  at 320, 390 and 1280 pixels, including pause, retry, ranking and enlarged text.
- `npm run test:spark-mechanics:browser --prefix mobilni-aplikace` renders the
  actual simulator/renderer at seven progression states at 320 and 390 pixels.
  Its isolated local preview uses real replay states, no application backend,
  and verifies that reduced motion preserves physical movement. Screenshots
  go to the system temporary directory or `APP_SCREENSHOTS`.
