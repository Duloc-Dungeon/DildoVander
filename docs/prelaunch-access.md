# Launch access and character portraits

Web and the Capacitor iOS application use the same `/app/v1/launch` policy.
Administrators manage beta and production schedules separately in **Správa →
Launch Management** (`/settings/admin/launch`). Gates start disabled to preserve existing accounts. Enabling a gate
without a date keeps that audience closed until an administrator supplies a date,
disables the gate, or explicitly unlocks it. Re-enabling a gate after launch can
remove existing user access; the editor warns before saving. Dates are persisted
as UTC milliseconds, with the administrator's display timezone recorded separately.
Europe/Prague changes in daylight saving time do not change the saved instant.

Owner, Administrator, Operator, Viewer and internal Tester accounts retain their
existing permissions. The Beta role is assigned by an administrator's verified,
single-use invitation; it grants the beta schedule, no staff permissions. An iOS
build, TestFlight installation, URL parameter or local clock never grants access.
Public registration is unavailable while the production audience is closed;
invitation enrollment and passkey sign-in remain available.

`/app/v1/admin/launch` GET/PUT returns and updates the configuration. PUT requires
a fresh passkey assertion, CSRF/origin validation, `X-DV-Confirm: yes`, and the
expected configuration revision. Conflicts return 409. Changes and actor IDs are
recorded transactionally in `ApplicationIdentity:DataRoot/launch.sqlite`, including
schedule, unlock and guest-copy changes. Preserve that database in the existing
identity data-root backups. No identity schema migration is required for Beta;
the existing role initialization adds it using the normal provisioning path.

## Guest journey and storage

An invitation opens the existing passkey enrollment flow. The guest supplies only
the fictional character's name and universe, and explicitly consents to sending
those details to OpenAI for enrichment and illustration. Saving is immediate;
the countdown never waits for a provider. The existing account-keyed character
table and optimistic revisions enforce one current character per account even
when several devices submit concurrently. Existing characters remain intact.

The minimal `/app/v1/prelaunch/character` GET/PUT exposes name, universe, revision,
job state, an optional safe loading sentence, and the authenticated portrait URL.
It does not expose research, sources, equipment, conversations or generated wands.
Existing feature APIs, guest routes and keepsake surfaces return a neutral
`launch_pending` response while access is closed. Verified device and operational
credentials retain their existing scoped APIs. Privacy, passkey management,
account deletion and deletion-status receipt flows remain available.

The reviewed nginx application profile also protects model pointers, geometry,
download indexes, manufacturing/source archives and legacy workshop ZIP URLs
through an uncached `/app/v1/launch/resource-access` authorization subrequest.
That endpoint returns 204/403 for nginx and validates current account roles.
Signed-in web and native model readers supply their existing account context;
anonymous requests regain access when production opens. Protected bytes use
`private, no-store`, including after launch, so postponing a launch cannot rely
on stale shared caches. Previously published or downloaded copies cannot be
retracted. The public application/source code remains inspectable.

Install the compatible backend and reviewed nginx profile together before enabling
gates. This source change does not modify installed host configuration. Independent
preview-model snippets must reference the same internal authorizer; their installer
rejects an application profile that lacks it. During a closed launch, model
publisher readback can use a separate, narrowly scoped credential: configure only
its SHA-256 as backend `Launch:ResourceReadbackTokenSha256` and provision the
matching server-side `KUFR_MODEL_READBACK_TOKEN` through the existing operator
secret mechanism. It authorizes only resource readback, no administrative APIs.
The helpers send it only over HTTPS and refuse redirects; never put it in command
arguments, repository files or chat. Existing legacy admin credentials work only
when that independent authority was already enabled; this feature does not enable it.

Portrait requests are opt-in rows in the existing `customer-characters.sqlite`.
They wait for the existing durable enrichment job and use its stored summary and
ambiguity information. The worker optionally generates a short sentence using
the project's existing `gpt-6-luna` model, then generates a vertical illustration
through the existing OpenAI SDK/credential path. Administrative creative prompts
are untrusted input beneath fixed safety instructions. Invalid, spoiler-containing
or failed messages leave the configured initial message in place. No conversation
memory or operational context is included in either provider request.

`CustomerCharacters:PortraitModel` selects the image model, falling back to
`Keepsake:ImageModel` and then `gpt-image-2.5-sunburst`. The existing backend
`OPENAI_API_KEY` is reused; no key reaches the client. Model availability and
billing remain subject to the configured provider account. See the
[official image API guide](https://developers.openai.com/api/docs/guides/image-generation).
No paid request is part of offline tests.

Images are validated, encoded as WebP and stored privately in the character
database. The owner-only portrait endpoint returns `private, no-store` and does
not accept an account ID or arbitrary file path. Profile deletion cascades to
portrait bytes atomically. Revision and lease checks reject late provider output
after editing or deletion. Portrait jobs survive restarts; expired in-flight image
leases become failed because billing may already have happened. A guest or admin
may explicitly retry, up to three image attempts for a revision. Enrichment retains
its existing three-attempt durable backoff. Admin artwork status is available at
`/app/v1/admin/launch/artwork`; retries and personalization previews use the same
recent-passkey/confirmation controls as launch mutations.

## Review and verification

App Review uses a dedicated synthetic account assigned the existing Tester role
through a real administrator invitation and passkey enrollment. This is an explicit
backend entitlement shared across all clients, with no secret build-dependent
bypass. Arrange durable reviewer sign-in instructions privately and test them
before submission. Do not publish account credentials or invitation tokens.

Suggested review notes (fill in private access instructions and tested devices):

> Ordinary invited accounts see a personalized character preparation screen until
> their scheduled opening. The supplied review account has explicit internal
> review access to the complete existing application before that date. This
> includes its real feature navigation and account deletion. The application uses
> OpenAI for fictional character research, optional loading text and illustrations.
> Hardware-dependent functionality requires the accessory; the accompanying
> demonstration and instructions explain how to evaluate it. Access instructions:
> [provide privately in App Store Connect].

Keep public metadata accurate about the actual product. Apple requires full
review access and working backend services; the countdown is not a substitute.
Follow the [submission runbook](app-store-release.md) and Apple's
[review guidelines](https://developer.apple.com/app-store/review/guidelines/).
No account was provisioned, service deployed or build submitted by this change.

Focused offline checks:

```powershell
dotnet run --project backend-tests -- --launch-policy
dotnet run --project backend-tests -- --character-portraits
dotnet run --project backend-tests -- --prelaunch-http
$env:DV_RUN_NGINX_LAUNCH_SMOKE = '1'
python -m unittest discover -s web/deploy -p test_launch_resource_gate.py
```

The identity CI suite includes all three. Provider output quality, actual account
model access, iPhone foreground/background behavior, invitation universal links
and App Review access still require configured-service and real-device acceptance.
Bundled client code is inspectable; access gates protect supported UI and server
operations, and do not cryptographically conceal an installed application's code.
