# Mobile game

The shared Vue/Capacitor application exposes the group game at `/play` through
customer navigation. It needs an authenticated Customer in an opened launch
group, without a suitcase, Bluetooth or camera. Legacy three-round guest duels
and their grants remain on `/archive/duel`.

## Rules and ownership

`backend/Features/Game/` owns `mobile-game-v0.3` and its SQLite state under the
existing account data root (`mobile-game.sqlite`). Clients submit operations
with an expected revision; scores, inventory, XP and unlocks belong to the server.
Pending opponent cards, bids and loadouts stay private. Late polls cannot replace
newer client snapshots.

| Mode | Authoritative behavior |
| --- | --- |
| Quick | Exactly seven exchanges. Strike beats trick, trick beats shield, shield beats strike. Ties consume an exchange without points. XP: 10 win, 6 loss/draw. |
| Sparks | Both players need two completed combat matches. Two sparks each for the whole match, at most one bid per exchange. Winner gains `1 + both bids`; ties consume bids. |
| Tactical | Both need 30 XP. Two four-exchange blocks, stock 2/2/2 reset between blocks. Sparks do not reset. XP: 20/12/12. |
| Loadouts | After both complete two tactical matches, both can opt in. Secretly confirm 2/2/2, 3/2/1, 1/3/2 or 2/1/3 before each block. Public stocks change only when both lock. |
| Teams | Four separate consents, all with 30 XP. Two simultaneous duels, swapping opponents after four exchanges. Reveal only when all four lock. Halftime transfers use frozen balances; reciprocal offers cancel and a recipient may hold three sparks. Eight exchanges, XP 20/12/12 each. |
| YOLO | Both consent after three completed combat matches. Bohumil Křivopísk invents the story and winner. No combat XP or combat-threshold progress. |
| Boss | Administrator previews and freezes the eligible active group's positive median XP. Energy 6/5, at most twelve exchanges. Damage `(1 + bids) × strength`, player strength frozen XP/reference. First valid victory rescues the group once; rematches never recapture the keeper. |

The boss first chooses uniformly from legal cards, then prefers the counter to
the previous revealed player card, then advertises strike/trick/shield/strike.
An available preference has probability 2/3; its sparks occur on exchanges four
and nine. A private seeded choice is stored before player input. Its seed
commitment is public initially; the seed is revealed after completion. Wand-I
attempts have unlimited basic actions and no player sparks, labelled exhibition.
All current boss attempts are outside any physical-prize competition.

Quick/tactical play is untimed: displayed durations are estimates. Either player
can abandon an unfinished match without winner/XP. Team halftime has a server
15-second deadline. Reconnection never manufactures a choice or substitutes for
an absent player. Ineligible peers release unfinished reservations without XP.

## Story, consent and privacy

Milestones derive from completed results; acknowledgement is separate from
earning an unlock. The home scene presents at most one new message, with deferral
and reopening. Cosmetic titles and YOLO ranking never increase combat strength.
The digital keeper uses recorded statistics and prepared text. The boss replaces
this game scene during capture; it does not take over the separate physical
suitcase conversation system.

Tasks need both players' opt-in. Adult requires each player's explicit 18+
declaration; Flirt additionally requires Adult and both Flirt choices. These are
declarations, not age verification. Only the completed non-draw loser's view
contains a task. Done/swap/skip do not affect XP or access. Either participant
can revoke Adult; a pending task then falls back to Classic.

YOLO uses the existing server `OPENAI_API_KEY` and bounded Responses client.
Only fictional name/world, bonded wand name and up to three whitelisted wand
traits enter the frozen snapshot. No descriptions, conversations, photographs
or source URLs enter it. Durable leases, recovery scans and atomic cancellation
protect one immutable valid verdict. Three Czech beats must cite allowed details
from both participants, with a participant winner and bounded verdict. Validation
does not prove humor or every narrative claim; inspect synthetic live examples.
Provider failure creates no winner or rewards. Live tests are explicit opt-in.

## API and verification

[`game-openapi-v1.json`](../contracts/game-openapi-v1.json) describes the account
API. Existing cookie, HTTPS/origin and CSRF checks remain. Per authenticated
account, game reads/writes have separate limits of 120/40 per minute, independent
of the shared sign-in allowance. Anonymous requests retain a bounded IP bucket.
Admin reveal needs a recent passkey and the reviewed median; a changed reference
requires a new preview. Account erasure fences new writes and removes game,
task, operation, boss, team and message data. Legacy duel documents are unchanged.
Web ingress and native transports allow only explicit paths and methods.

Run from the repository root:

```text
npm test --prefix contracts
npm test --prefix mobilni-aplikace
npm run build --prefix mobilni-aplikace
npm run test:game:browser --prefix mobilni-aplikace
dotnet build backend-tests/BackendTests.csproj --no-restore
dotnet run --project backend-tests/BackendTests.csproj --no-build -- --mobile-game
dotnet run --project backend-tests/BackendTests.csproj --no-build -- --mobile-game-http
dotnet run --project backend-tests/BackendTests.csproj --no-build -- --mobile-game-boss
dotnet run --project backend-tests/BackendTests.csproj --no-build -- --mobile-game-teams
dotnet run --project backend-tests/BackendTests.csproj --no-build -- --mobile-game-progress
```

`npm run preview:game --prefix mobilni-aplikace` serves labelled synthetic scenes
on loopback with no live identities, providers or hardware. Browser checks cover
four phone widths, 200% text, reduced motion and key interactions on the compiled
bundle. Character masters and runtime exports are documented in
[`design-system/assets/game/`](../design-system/assets/game/README.md).

The separately approved live test is `--mobile-game-yolo-live`; never add it to
ordinary CI. Deployment, signed native builds, device screen-reader acceptance,
human playtesting and physical-prize rules remain separate gates.
