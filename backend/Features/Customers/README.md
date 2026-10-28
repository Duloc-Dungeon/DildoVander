# Account-owned character and conversation feature

The existing application identity feature owns passkeys, account sessions, CSRF,
staff roles and resource grants. Public enrollment creates only `Customer` after
an owner exists. Customer ownership always comes from the authenticated account,
never an account identifier supplied in a body or a character's name.

`/app/v1/customer/character` stores exact `userInput` separately from enrichment.
Writes require the current revision and profile identity. An account has one
active character; `newCharacter` cannot replace an existing identity. Customers
create their first character; only an administrator can edit or refine it.
Customers can independently set or clear an optional display label (80 characters)
with `PUT /app/v1/customer/character/label` using `profileId`, `label` and
`expectedLabel`. A stale label/profile returns 409. This does not change the
profile revision, enqueue AI work or require additional AI consent. The additive
`character_labels` table stores it outside `userInput`; deleting the profile
cascades to its label. Game lobby, match and ranking responses resolve current
labels for display while persisted match names and AI context remain unchanged.
Testing characters have equivalent isolated metadata at
`PUT /app/v1/testing-sessions/characters/{id}/label`.
Registration requires affirmative processing consent, stored with a timestamp
and version on the account. Existing accounts confirm once before further AI
work; the migration never infers consent. Endpoints derive processing permission
from this record rather than a later client checkbox. Personal memory and photo
use keep their separate optional confirmations. Correction creates a new
revision of the same identity. Queued/running
enrichment does not block conversation start: the captured input is sufficient.
The worker uses leased, revision-fenced jobs, a 45-second provider deadline and
at most three attempts with durable backoff. It records token usage without
keeping fictional text in the usage ledger. Only actual provider citation URLs
can support web facts. No cross-account enrichment cache is implemented.

`ApplicationIdentity:DataRoot/customer-characters.sqlite` is an additive feature
store; the additive Identity migration stores account and ceremony consent
without changing existing roles or grants.
Tables are created idempotently. The operation journal adds customer tickets,
immutable session snapshots, revocation tombstones and a shared device lease.
The archive adds a private customer ownership table outside public story data.
Keep all three existing data roots and data-protection keys in normal backups.

Profile removal and its deletion intent commit together. Replay applies journal
revocation and live-context invalidation idempotently, then marks the intent
complete. Failure leaves it pending across restart. Permanent tombstones reject
provider startup even before replay succeeds. Account deletion also fences new
profile writes. Removing a profile never deletes a keepsake archive. Ordinary
account disablement requests an end without erasing archived private data.

`CustomerCharacters:Model` defaults to the existing Responses dialog model;
`CustomerCharacters:WebSearchEnabled` defaults to true. Provider credentials use
the existing backend `OPENAI_API_KEY` configuration and never reach the phone,
BLE or firmware. A missing credential produces a failed enrichment job while
the saved profile remains usable. Deterministic tests use a fake provider.

See the [protocol, recovery and rollback contract](../../../contracts/customer-sessions.md)
and [BLE wire schema](../../../contracts/customer-ble-v1.schema.json). Admission
defaults off and requires both protocol-v1 backend and compatible firmware.
Disable admission and reconcile devices before rollback; retain additive tables.
An old backend must not accept legacy starts while a customer lease is active.

Validation entry points are `dotnet run --project backend-tests --
--customer-profiles` and `--customer-sessions`, the existing identity/memory/
keepsake suites, `npm test` in `contracts/`, the Vue typecheck/build/browser suite,
the macOS native test script and the production firmware build. The contract
contains the real iPhone/ESP32 acceptance procedure; offline tests do not prove
physical BLE, autonomous audio or actual provider behavior.

Customer start tickets require the recorded registration consent for voice
audio, transcript and character context. The server records that proof as
`openAiConsent: true` on the immutable ticket. Personal memory remains a separate
optional consent. Issuance, redemption and provider context reject missing consent;
old clients must update and legacy unaccepted tickets must be issued again after
consent. The BLE redemption shape is unchanged.

The account-scoped conversation-preferences GET/PUT endpoint stores a validated
Czech/English language and gentle/cheeky/adult-roast ceiling for the current owned
profile. First ticket acceptance freezes those preferences independently of the
phone's interface language. Provider retries retain the conversation's current
opt-outs. See [guest preferences](../../../docs/customer-preferences.md) for
revision checks, export behavior, native route bounds and focused validation.

Portrait generation and welcome text are independent results: failed research
can use the saved fictional brief with no verified facts, and image failure
retains an already generated joke. The owner-scoped retry endpoint accepts
`expectedProfileId` and `expectedRevision`, queues a missing/failed portrait and
never changes the character identity or regenerates a ready image.

Welcome authoring uses the Czech roast/absurdity instructions in
`CharacterWelcomePolicy.Prompt`. A teaser is optional and must match the one
approved theme when present; omission requires empty `teaser` and null `teaserId`.
Without verified facts, the writer receives an exact identity sentence containing
only the supplied name and world (or an empty sentence when it exceeds the text
limit or fails the display policy). Other unverified biography is rejected. Existing saved welcomes are not
regenerated merely because the authoring instructions change.

## Administration of characters and portraits

Owners and administrators open `/settings/admin/characters` from account
administration, the launch page or the customer-session overview. The list loads
50 compact records at a time; search covers the loaded records. Selecting a
character fetches its current detail. Operators and customers cannot use these
administrative endpoints.

| Endpoint | Contract |
| --- | --- |
| `GET /app/v1/admin/characters` | `{ items, nextCursor }`; summaries contain profile ID, revision, name/world, status and update time. Continue with `?after=<nextCursor>` (32 lowercase hex characters). |
| `GET /app/v1/admin/characters/{profileId}` | Full current profile and `portrait: { status, hasImage, errorCode, updatedAt }` or null. |
| `PUT /app/v1/admin/characters/{profileId}` | `{ expectedRevision, userInput: { name, world, version, description, language } }`; returns the updated detail. |
| `GET /app/v1/admin/characters/{profileId}/portrait?revision=N` | Current revision's private WebP image; obsolete or missing revisions return 404. |
| `PUT /app/v1/admin/characters/{profileId}/portrait?expectedRevision=N` | Raw PNG, JPEG or WebP body; returns the updated detail. |

Writes require recent passkey verification, `X-DV-Confirm: yes`, and the existing
account session/origin/CSRF checks. Both mutations atomically advance the profile
revision and reject an outdated revision with `character_revision_conflict`
(409). The editor preserves the draft and offers a reload; reauthentication does
not replay a write. Account changes abort reads and writes and clear private
drafts, images and object URLs. All administrative responses are private/no-store.

Manual edits do not enqueue AI work. Changed input clears the old enrichment;
finished portraits remain attached to the new revision. Portrait replacement
preserves finished enrichment and replaces the image, clearing old welcome text
and attribution. Both mutations invalidate queued/running jobs and portrait
leases so late workers cannot overwrite the administrator's changes. Existing
captured conversation snapshots and saved keepsake copies remain unchanged.
Deleted/fenced accounts are excluded from reads and mutations.

Uploads are bounded to 8 MiB, checked against the actual image format and decoded
within 4096 pixels per side and 16 million pixels total. Animated/malformed images
are rejected; accepted images are re-encoded as WebP. Upload errors use
`invalid_character_portrait` (400/415) or `character_portrait_too_large` (413).

Focused checks: backend harness `--character-administration` (also included in
`--identity`), frontend component `CharacterAdminView.spec.ts`, transport policy
tests, and `npm run test:character-admin:browser --prefix mobilni-aplikace` after
the frontend build. The browser check uses synthetic accounts/media and checks
390/1280 px layouts, edits, uploads and revision conflicts. Native device and real
passkey ceremonies require their normal platform acceptance checks.

The phone's `/app/v1/customer/wand` flow provides a short live voice conversation
for the first digital wand and its upgrade. It uses account-bound, short-lived
socket tickets and persisted selections; it cannot operate a physical device or
fabricate a physical keepsake. A stored wand is required for gameplay, and the
upgrade is required for tactical play. Offline checks include `--mobile-wands`;
the character browser suite exercises portrait recovery and the first-wand gate.
