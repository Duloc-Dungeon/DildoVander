# PR validation and main release

The full `development` → `main` PR validates all seven active application projects.
Physical firmware, CAD and the hardware simulator are paused and excluded from automatic PR/release jobs.
An explicit manual `ci.yml` request with `project=firmware` and the separate
manual CAD workflow remain available; `project=all` does not include hardware.
After every selected project and the secret scan pass, `CI required` records
`pr-validation.json` in the attempt-specific `pr-validation-N` artifact for
14 days. Metadata-only events, stacks, forks and partial checks cannot produce
release test evidence.

The receipt identifies the repository, PR, run and exact attempt, event head and
base, and the actual synthetic merge commit/tree checked out by the jobs. Its
checkout must have the event base and head as its two parents. A head SHA alone
does not prove that the merge with main was tested.

## Release decision

A direct current-main `ci.yml` run for `project: all` may reuse these test
results. The decision is internal; there is no user-controlled skip-tests input.
The verifier requires all of the following:

- One canonical merged development PR whose merge commit is the current main.
- The newest candidate PR CI run for that head, with the expected workflow,
  canonical repository, completed success and exact latest attempt. It does not
  fall back to an older green run after a newer failure.
- One unexpired immutable receipt artifact for that attempt, with its artifact
  ID and SHA-256 identity retained across verification.
- Receipt identity and Git object tree/parents matching the final main merge.
- Successful attempt-specific jobs and required test steps, including the
  receipt recording/upload steps. A successful job with skipped required tests
  is insufficient.
- The same current main, candidate, artifact and attempt after verification.

Discovery errors, download failures, missing/expired receipts, unsupported merge
lineage, partial lists and any mismatch run the complete suite. They never
authorize skipping tests. The first release without suitable evidence therefore
retains full validation.

## What still runs on main

With valid evidence, main builds the backend, web and site payloads
again, preserving the exact main commit and existing package provenance. It
retains history secret scanning, workflow/whitespace checks, production builds,
fresh backend HTTP smoke and isolated guest integration, compiled-site input
checks and normal package/signature verification.
The backend diagnostic assembly is still built and packaged.

Previously completed application unit and browser suites are
not repeated. Unsigned iOS simulator/debug Android validation and the offline AI
evaluation job are omitted from this artifact-only main CI run after their full
PR coverage has been proved. iOS distribution workflows still compile/sign the
native distribution and keep their release-specific checks. Android distribution
retains its independent signed workflow.

The private product controller no longer requires a firmware artifact or starts
firmware publication. Its standalone manual firmware path remains independent.
The backend does not compile or bundle the portable simulator by default.
Simulator-specific tests remain available manually; active product checks run
without a C++ compiler.

PR artifacts are never promoted to production. `main-release.cjs`, the app and
private `verify-build.cjs` guards, private deployment workflows, signing
credentials and release serialization retain their existing contracts. The main
controller continues to correlate a genuine direct-main CI child and signed
children. The default local preflight is a fast backend/npm subset. Local reports cannot
authorize test reuse or deployment; the complete PR CI receipt remains required.

The optimization becomes available when this source change is later promoted to
main. Delivering it into development does not trigger a release or deployment.

## Catch failures before promotion

Development PRs now run the existing affected-product workflows (including full
backend and site/browser suites when selected), not only repository and secret
checks. `Development required` requires success for selected jobs and skips only
unselected ones. The release PR still runs the complete release matrix and
current-main artifact provenance checks; development runs cannot replace them.

Development allows squash for feature PRs and merge commits for main-to-development
synchronization. After a release, open a `main` → `development` PR, wait for its
required checks and merge it with **merge**, never squash. This preserves release
ancestry without temporarily editing protections. Do this before the next release
PR. Required status checks, PR review resolution and force-push restrictions remain.
Apply the reviewed ruleset through the existing repository-settings procedure.

Explicit portrait acceptance (paid, never normal CI): set `OPENAI_API_KEY` in the
process environment, then run `dotnet run --project backend-tests --
--character-portrait-live <output.webp>`. This generates one synthetic Harry Potter
portrait using the actual application provider. Inspect the saved image for round
glasses, black hair and forehead scar; a passing HTTP call alone does not establish
recognizability. No customer records are read or rewritten.
