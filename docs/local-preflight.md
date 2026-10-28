# Local pre-PR validation

Run `python scripts/ci/preflight.py --dry-run` from the repository root to inspect the default host-local plan. Run it without flags to execute that plan. The runner checks Node against the exact `contracts/.nvmrc` pin (24.21.0) and checks the SDK selected by `global.json` (10.0.303 with `latestPatch`). It reports mismatches as failed checks. The runner does not install dependencies unless `--install-dependencies` is supplied; installs use each repository's existing lockfile via `npm ci`, then install Playwright Chromium for the browser journeys.

## Scopes

- `--scope local` includes portable repository, design, contracts, frontend, backend harness and browser checks, plus native checks selected for the current host. Android checks are included because the Android Gradle project can be built from supported development hosts. Missing tools are reported against their check and make the command exit nonzero; nothing is silently treated as passed.
- `--scope all` runs available checks and requires Linux, macOS/iOS, Android and firmware coverage. A platform check that cannot run on this host or lacks required tooling is recorded as missing, and the command exits nonzero. Linux and macOS jobs need their matching hosts. Android requires JDK 21 and Android 36/build-tools 36.0.0, discovered from the same SDK locations as `scripts/android.py`. Firmware builds require PlatformIO and its configured toolchains. Run `--dry-run --scope all` to review the checks without building or writing files.
- Repeat `--group` with `--scope local` to select a practical subset, for example `--group repository --group contracts --group frontend --group browser`. Groups are `repository`, `design`, `contracts`, `frontend`, `backend`, `browser`, `android`, `ios`, `firmware`, `site`, `coverage` and `setup`. The all-scope rejects group filtering so its platform requirements remain strict.

The shared checks follow repository CI commands: `scripts/ci` tests, actionlint, Gitleaks tests and history scan, pure Python `ai-eval` tests, model/CAD and site viewer fixture tests, design token and integrity checks, contracts tests, mobile Node/Vitest tests, typecheck and production build. Backend validation includes the executable harness groups, offline lifecycle/client/deployment tests, a local temporary publish, HTTP smoke, and keepsake integration with explicit absolute `KUFR_BACKEND_DLL` and `KUFR_TESTS_DLL` paths. The account browser fixture receives the built Release harness DLL and reports it missing if the build did not produce it. Locked runtime assets support Linux x64 and Windows x64; macOS uses the framework-dependent `net10.0` harness without an invented RID. Browser journeys require compiled web output and Playwright Chromium. iOS commands run from the mobile project root, sync/generate native assets, resolve Swift packages and build an unsigned simulator app. Firmware Python, Node, compiler-discovered simulator and PlatformIO checks run locally when their tools exist.

The firmware workflow has additional inline GNU C++ harnesses not individually represented in this runner. `--scope all` reports that Linux firmware coverage gap as missing even on Linux, so it cannot issue a complete CI coverage claim until those harness commands are added. Local scope still runs the portable firmware checks and reports `ci_coverage_complete` as false.

The `local` scope's `complete` field means the selected local plan passed; `ci_coverage_complete` remains false because other platform jobs were not run. `all` needs matching platform hosts and their toolchains for complete CI coverage. No live-provider smoke, signing, production deployment, device flashing or artifact upload is part of the plan. Local site/backend packages and published binaries are written under the checkout-specific system temp directory. The runner selects only locked Linux x64 and Windows x64 .NET RIDs; other hosts use the framework-dependent `net10.0` target.

## Reports

Use `--report` to write a JSON report to a checkout-specific directory under the system temp directory; provide a custom path only outside the checkout. It records starting and ending HEAD/tree/worktree fingerprints, host architecture, scope, exact commands, versions, outcomes and missing coverage. The run fails if source state changes while checks execute. Reports are bound to the ending checkout state. `--check-report PATH` accepts a current, complete local or all report with no missing checks; it rejects group-filtered partial runs. Use `--scope all --check-report PATH` when full CI platform coverage is required. A complete local report never claims that unexecuted Linux/macOS jobs passed.

Examples:

```powershell
python scripts/ci/preflight.py --dry-run
python scripts/ci/preflight.py --scope local --group repository --group contracts --group frontend --group browser
python scripts/ci/preflight.py --scope all --install-dependencies --report
python scripts/ci/preflight.py --check-report "PATH_FROM_REPORT_OUTPUT"
```
