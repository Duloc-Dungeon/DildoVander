# Local pre-PR validation

Run `python scripts/ci/preflight.py --dry-run` from the repository root to inspect the default quick plan. Run it without flags to execute that plan. The runner checks Node against the exact `contracts/.nvmrc` pin (24.21.0) and, when backend checks are selected, checks the SDK selected by `global.json` (10.0.303 with `latestPatch`). It reports mismatches as failed checks. The runner does not install dependencies unless `--install-dependencies` is supplied; quick installs use the contracts and app lockfiles, while full installs also include the design system and Playwright Chromium.

## Profiles and scopes

- `--profile quick` is the default local PR loop. It checks pinned Node versions, Git diff formatting, workflow contracts and actionlint, four short Python helper suites, contracts npm tests, app npm tests/typecheck. When backend inputs changed, it also checks the .NET pin, performs one locked restore/build, and runs backend lifecycle tests and six curated offline backend harness groups. It skips the full backend harness, HTTP/integration suites, production build/publish, design/browser/site/native-platform validation and other longer checks.
- `--profile full --scope local` runs the broader local suite: repository/security checks, design, contracts, frontend build, backend harness/client/deployment/package/HTTP checks, browser journeys, site packaging, and host-supported Android/iOS checks. Missing tools are reported against their check and make the command exit nonzero; nothing is silently treated as passed.
- `--profile full --scope all` adds the Linux, macOS/iOS and Android platform coverage and requires matching hosts/toolchains. A check that cannot run on this host or lacks required tooling is recorded as missing, and the command exits nonzero. Linux and macOS jobs need their matching hosts. Android requires JDK 21 and Android 36/build-tools 36.0.0, discovered from the same SDK locations as `scripts/android.py`. `--scope all` requires the full profile. Use `--dry-run` to review either plan without building or writing files.
- Repeat `--group` with `--scope local` to select a practical subset, for example `--group repository --group contracts --group frontend`. Groups are `repository`, `design`, `contracts`, `frontend`, `backend`, `browser`, `android`, `ios`, `site`, `coverage` and `setup`. The all-scope rejects group filtering so its platform requirements remain strict.

The shared checks reuse application CI commands: `scripts/ci` tests, actionlint, Gitleaks tests and history scan, pure Python `ai-eval` tests, design token and integrity checks, contracts tests, mobile Node/Vitest tests, typecheck and production build, web/site input and packaging tests. Physical firmware builds, CAD/effects/model fixture checks and the software twin simulator are intentionally outside the default PR preflight while those products are inactive; their existing source workflows remain available for manual use. The backend harness validation here omits simulator-dependent Twin groups.

Full-profile backend validation includes the executable harness groups, offline lifecycle/client/deployment tests, a local temporary publish, HTTP smoke, and keepsake integration with explicit absolute `KUFR_BACKEND_DLL` and `KUFR_TESTS_DLL` paths. The account browser fixture receives the built Release harness DLL and reports it missing if the build did not produce it. POSIX memory-provisioning tests use `os.getuid` and run only on Linux; Windows local scope omits that check, while `--scope all` reports the missing Linux coverage. Locked runtime assets support Linux x64 and Windows x64; macOS uses the framework-dependent `net10.0` harness without an invented RID. Browser journeys require compiled web output and Playwright Chromium. Their screenshots are redirected to the checkout-specific system temp directory so a run does not modify source state. iOS commands run from the mobile project root, sync/generate native assets, resolve Swift packages and build an unsigned simulator app.

Local packaging invokes the repository's `package.py` through `local_package.py`. Its manifest records the actual Git HEAD and tree, with `event=local_preflight`, `repository=local/preflight`, and run id/attempt `0`; inherited GitHub Actions variables are discarded. This artifact is for local validation only and does not claim GitHub workflow or release provenance.

The report records its profile. A passing quick run has `complete=true` for its selected checks but always has `ci_coverage_complete=false`. A quick report can satisfy a quick check, but `--profile full --check-report PATH` rejects it. Group-filtered reports are partial and cannot satisfy full-report checks. Full `all` needs matching platform hosts and their toolchains for complete application CI coverage. No live-provider smoke, signing, production deployment, device flashing or artifact upload is part of either profile. Local site/backend packages and published binaries are written under the checkout-specific system temp directory. The runner selects only locked Linux x64 and Windows x64 .NET RIDs; other hosts use the framework-dependent `net10.0` target.

## Conditional backend checks

Quick mode compares the whole branch against the merge base with cached
`origin/development`, plus staged, unstaged and untracked changes. Use `--base REF`
for a different PR target; fetch the target before validation. A missing base
fails selection instead of silently skipping tests.

Backend checks run for `backend/`, `backend-tests/`, shared `contracts/`, .NET
build/SDK/NuGet inputs and the external prompt/catalog/font/token inputs consumed
by the backend. Frontend-only, workflow and Markdown documentation edits skip every backend
check, including restore, build and the SDK probe. Embedded SealedCase Markdown
is runtime content and still requires backend checks. The normal quick plan has 11
checks without backend inputs and 21 with them. Explicit `--group backend` forces
the quick backend checks for diagnostics; full mode always retains backend checks.

Dry runs and reports record the base revision, changed paths and backend decision.
Changing the comparison base invalidates a saved quick report, even when the source
HEAD did not change. No GitHub/release validation gate is relaxed by this selection.

## Reports

Use `--report` to write a JSON report to a checkout-specific directory under the system temp directory; provide a custom path only outside the checkout. It records starting and ending HEAD/tree/worktree fingerprints, host architecture, scope, exact commands, versions, outcomes and missing coverage. The run fails if source state changes while checks execute. Reports are bound to the ending checkout state. `--check-report PATH` accepts a current, complete local or all report with no missing checks; it rejects group-filtered partial runs. Use `--profile full --scope all --check-report PATH` when full CI platform coverage is required. A `--profile full --check-report PATH` check rejects quick-profile evidence. A complete local report never claims that unexecuted Linux/macOS jobs passed.

Examples:

```powershell
python scripts/ci/preflight.py --dry-run
python scripts/ci/preflight.py --profile full --scope local --dry-run
python scripts/ci/preflight.py --scope local --group repository --group contracts --group frontend
python scripts/ci/preflight.py --profile full --scope all --dry-run
python scripts/ci/preflight.py --profile full --scope all --report
python scripts/ci/preflight.py --profile full --scope all --check-report "PATH_FROM_REPORT_OUTPUT"
python scripts/ci/preflight.py --check-report "PATH_FROM_REPORT_OUTPUT"
```

## Manual hardware simulator validation

The paused suitcase simulator is opt-in. On Linux x64 with a C++ compiler:

```sh
dotnet restore backend-tests/BackendTests.csproj -p:SelfContained=true --locked-mode
dotnet build backend-tests/BackendTests.csproj -c Release -r linux-x64 -p:SelfContained=true -p:IncludeHardwareSimulator=true --no-restore
dotnet run --project backend-tests/BackendTests.csproj -c Release -r linux-x64 --no-build --no-restore -- --hardware-simulator
```

The separate `--twin` and `--twin-media` harness groups use `KUFR_TWIN_TEST_NATIVE`
pointing to the executable built by `firmware/simulator/native/build_host.py`.
`--firmware` retains the firmware release checks. These manual groups are outside
normal product validation. Persona Lab product checks remain in the default CI
matrix. The opt-in build does not authorize firmware publication or deployment.
