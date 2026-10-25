# Wand Night Stand consumer experience

The public name is **Wand Night Stand**. Repository, solution/project, namespace,
service, API, storage, protocol, infrastructure, URL and bundle identifiers retain
their existing names. The App Store pipeline continues to require
`de.dildovander.companion`; the separate unsigned/Ad Hoc identity is unchanged.

## Implemented journey and boundaries

| Audience and state | Experience | Boundary |
| --- | --- | --- |
| Anonymous, audience closed | Sign-in or private invitation | No open registration or feature navigation |
| Customer, production closed | One current character, fictional name and universe, explicit AI consent, private portrait job and countdown | Minimal prelaunch DTO; no enriched lore, sources, wand outcome or keepsake spoilers |
| Beta customer, beta closed | The same preparation journey | Beta is an audience, never a staff entitlement |
| Customer or Beta, audience open | Character, own sessions, keepsakes and account | Existing ownership, consent and edition authorization |
| Tester and App Review | Existing explicit Tester bypass, synthetic review journey and diagnostics | No implicit device operation permission; no secret reviewer build |
| Operator or Viewer | Existing launch bypass and role navigation | Device/resource grants still apply |
| Owner or Administrator | Launch editor, guest message, artwork jobs and audit | Existing recent-passkey, revision, CSRF and confirmation checks; only Owner issues account invitations |

The audience gate remains shared by web and iOS. The distributions can be released
separately; installing a native build does not confer a backend entitlement.

### Audit reconciled with source

| Existing behavior or gap | Delivered behavior and source |
| --- | --- |
| Invitation-only passkey enrollment; no open signup | Preserved capability/role checks; satirical invitation copy in `SignInView.vue` and `customerCopy.ts` |
| One current profile with asynchronous enrichment and portrait jobs | Preserved job lifecycle and consent; phased preparation in `LaunchView.vue`, `CharacterPortraitProvider.cs` and `CharacterProfileStore.Portraits.cs` |
| Portrait cache previously depended on revision alone | Owner/profile/revision identity, abort/decode fencing and bounded retry in `privateCharacterPortrait.ts`; matching native transport policy |
| Existing release policy separates audiences, not platforms | Preserved launch settings, review/tester bypass and distribution workflows; no platform entitlement added |
| Public fictional identities and generated wand art already have explicit approval | Duel narration uses that approved context; owner portrait remains private in `PrivateCharacterAvatar.vue` |
| Duel story, account history and downloads could diverge | Persisted commentary attached to authorized history and reused by UI/text/PNG exports |
| Physical narration modes existed in stored duels | New `DuelShowSlot` sessions bind text only; historical device modes remain readable |
| Case persona lacked a durable recurring reason for the absent seller | Server persona plus evening-scoped idempotent scheduling in `EveningMemoryStore.CaseVoiceAsides.cs`; no firmware protocol change |

Negative coverage includes unauthorized invitation/admin access, closed-audience
DTOs and route navigation, invalid side grants, locked duel choices, unapproved
fiction/art withdrawal, stale/deleted portraits, late image decoding and account
switches. Tester labels stay on authorized diagnostics, not guest preparation.

Invitation enrollment uses the existing single-use capability and passkey flow.
Character preparation retains optimistic revisions, persistent jobs, explicit
provider consent and bounded portrait retries. The curtain is decorative brand
art, never a replacement presented as the guest's generated portrait. It remains
visible while a private portrait loads or fails. Generated images retain their
existing authenticated transport, object-URL cleanup and lifecycle checks.

Preparation starts with the administrator's editable hungover-Olivander message.
Research runs in the background, then a bounded character-specific loading line
acknowledges the fictional persona through suspense. Guests see no research,
painting or numbered process steps, and the primary action does not announce the
portrait. The full OpenAI data-use explanation remains available beside explicit
consent. Generated and historical public-facing loading copy is filtered before
reveal; raw administrator settings remain editable. The final mild roast appears only after the
owner's image has decoded. Text and image publish together under the existing job
lease; stale profile revisions, deleted profiles and switched accounts cannot
reuse another portrait. A failed text request uses bounded Czech copy; image
retry remains explicit. Postlaunch the owner reuses that same private portrait
without another generation call. Public duels retain separately approved public
wand art; private character portraits are not automatically published.

After opening, the guest keeps one current character while retaining historical
keepsakes. Customer hardware start remains iOS-native BLE with existing
device/operation-bound tickets. Web never claims to provide native pairing.
Spoken case dialogue uses DV01 and GPT Live exclusively. Autonomous generated
wand selection stays server-owned; the redesign introduces no manual catalogue-
selection claim. Retired-engine sessions remain readable with upgrade guidance.

Duels are text only. A bounded Responses request narrates the three authoritative
rounds using approved public fictional identities; it cannot change points,
choices or winners. A stored narrative also appears in authorized history and
exports. Generation failure keeps the deterministic story readable. New duels
create no voice session or physical cue. Existing historical physical modes stay
readable. Completed non-draws receive one durable catalog party task, with a
nonalcoholic/seated alternative and a skip action. A dry roast acknowledges the
easier choice; no choice changes scores or requires task completion.

The invitation persona is hungover; the party persona describes Olivander as
drunk and the speaking suitcase as his stand-in. Eligible production voice
openings schedule that aside with an initial 1-in-10 chance and increasing odds
after misses, guaranteed by the tenth opening. Durable receipts prevent reconnect
rerolls, reset after a hit, and guarantee at least two scheduled asides per twenty
eligible sessions in an evening. Lab sessions and duels do not count. This is a
prompt-scheduling guarantee, not proof of audible delivery.

## Visual source and touchpoints

[DESIGN.md](../DESIGN.md) records the ink, brass, velvet and warm-paper direction.
The consumer CSS adapts existing `--aw-*` roles and components rather than changing
the engineering design-system generator. Persisted theme IDs stay compatible;
visible names are Night salon and Velvet evening (Czech equivalents in Czech UI).

The identity appears in the shared shell and route titles, prelaunch and
invitation screens, character/account/keepsake surfaces, archive empty state,
home art, support/privacy, exported duel labels, tester/Persona Lab speech labels,
native display and permission text, native app icon/launch mark, and standalone
iPhone installer. The configured GPT Live persona introduces the same public
name. Firmware already receives server-authored speech/audio: no control, sound,
LED, electrical or BLE change is needed for this naming change.

The default language is Czech. Interface copy on the countdown is Czech/English and uses the existing account-
scoped local language preference. Administrator-authored event messages and
personalized job messages remain in their original language; English identifies
them as host notes. Character names and universe strings are never translated.

## Assets and generation

The exact prompts, origin, dimensions and checksums are in
[`manifest.json`](../mobilni-aplikace/src/assets/brand/manifest.json).

| Asset | Production use | Delivery |
| --- | --- | --- |
| Original curtain, crescent clasp and night flower | Prelaunch, onboarding/invitation and portrait waiting/error fallback | Responsive 960/480 px WebP, about 71/19 KB |
| Original wand/paper/night-flower still life | Postlaunch home and unfiltered keepsake empty state | 960 px WebP, about 59 KB; lazy on empty archive |
| Full-name editorial logo with wand underline | Wordmark companion, favicon, app icon and splash | Opaque RGB 1024 PNG master, 256 WebP and 180 PNG derivatives |
| Cormorant Garamond 500 | Display headings and wordmark | Self-hosted TTF, SIL OFL 1.1 included in bundle notices |
| Existing Inter and IBM Plex Mono | Body, controls, numeric data and diagnostics | Existing local assets and licenses |

The ImageGen artwork is original to this rebrand, with no sourced franchise art.
WebP derivatives preserve the artwork and have prompt provenance sidecars. Native
assets use `node mobilni-aplikace/scripts/generate-native-assets.mjs`; all existing
iOS build workflows run it after Capacitor sync. Generated PNGs stay ignored.
Native launch RGB is generated from the consumer background override, with the
shared token fallback for engineering-only fixtures. The installer
retains its standalone embedded fallback and an inline consumer palette subset.

## Acceptance and verification limits

Relevant checks are the app unit/component suite, compiled application browser
suite, launch browser suite (390, 820, 1440 and 320 px English), tester browser
suite, native preparation/installer tests, site packaging with the real dist,
design-token drift check, and backend `--launch-policy`, `--prelaunch-http`,
`--character-portraits`, `--persona-lab`, `--case-voice-asides`, `--duel` and
`--conversation-engines` runners. The backend test project is a
console test runner; `dotnet test` alone does not execute these suites.

Browser fixtures establish presentation, access navigation, loading/retry states,
keyboard/zoom and request boundaries. They do not establish paid-provider output
quality, production gate configuration, durable Apple reviewer credentials,
VoiceOver, physical iPhone BLE/reconnect/audio/LED acceptance, or a signed release.
Run the existing macOS/iOS checks and real-device acceptance before publication.
An open-source frontend and already downloaded material cannot promise secrecy;
server authorization remains the boundary for private guest data and operations.
