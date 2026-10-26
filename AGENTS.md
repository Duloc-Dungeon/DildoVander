

<!-- hermes-project-workflow:start -->
# VPS workspace

Native Hermes owns this project. Read the selected repository AGENTS.md. Use an isolated native Git worktree under /workspace/worktrees/dildovander/<task> and a codex/ task branch before source edits. Reuse this task's worktree on resumption; preserve canonical repositories, concurrent changes and other task worktrees. Serialize overlapping writes/builds and obtain focused independent review for material delivery. Windows-only parent startup hooks and absolute Windows paths do not run here. Explicit Windows permission applies only to that task/thread through windows_work. Keep task progress in its Hermes session/goal; validated lessons belong to the Hermes lead. Retired Engram writer/recall instructions are replaced by the VPS memory authority.

This parent is not a Git repository. DildoVander-app is the product repository; DildoVander-deploy is the private operations repository. Use separate task worktrees for each repository actually touched. .NET SDK 10.0.303 and Node 24 are installed. Run GitHub CLI through python3 /workspace/home/.local/share/github-account-routing/github_accounts.py gh ... from the target repository. Do not switch shared accounts. Load dildovander-ops for project context; ops_call administers production directly. Source work does not authorize deployment, external mail or unrelated live changes.

<!-- hermes-project-workflow:end -->

## Project map

Wand Night Stand is a mobile party experience: invitations, characters, shared evenings, games and keepsakes. The app README says physical suitcase development is paused; retained ESP32-S3 firmware and simulator code are technical background, not the default mobile UI scope. Product source combines Vue 3/TypeScript/Vite, Capacitor iOS/Android and an ASP.NET Core/SQLite backend. Private deployment source is separate.

- In `DildoVander-app/`, `mobilni-aplikace/src/application/` owns the shell/routes; `src/features/` contains feature UI and state; `src/platform/` contains platform integration.
- `backend/Features/` contains backend features; `backend-tests/` is an executable C# test harness; `contracts/` owns schemas, OpenAPI and generated validators.
- `firmware/` is the ESP-IDF PlatformIO project; `firmware/simulator/` contains its native bridge and Node simulator. `web/` contains packaging/deployment helpers.
- In `DildoVander-deploy/`, read `README.md` and `AGENTS.md`; workflows and `scripts/ci/` enforce artifact provenance and private delivery boundaries. Keep private host configuration and guest data out of public app source.

## Dev environment

- Product toolchains: .NET SDK `10.0.303` with `latestPatch` roll-forward (`global.json`); Node `24.21.0` (`mobilni-aplikace/.nvmrc`). npm packages have separate lockfiles, not a root npm workspace.
- Run app commands from the selected product checkout/worktree, not this parent. Install both dependency trees: `npm ci --prefix contracts` and `npm ci --prefix mobilni-aplikace`.
- From `mobilni-aplikace/`, `npm run dev` starts Vite bound to `127.0.0.1`. Native iOS compilation/signing requires macOS/Xcode, not this Linux sandbox.

## Build & test

Commands below are evidenced by package scripts and workflows; they are not a claim that the suites have passed locally.

- From `mobilni-aplikace/`: `npm test` runs Node tests followed by Vitest; `npm run typecheck` runs `vue-tsc --noEmit`; `npm run build` checks generated contracts, typechecks, copies notices and builds Vite.
- From `contracts/`: `npm test`; regenerate validators with `npm run generate`, or check freshness with `npm run check:generated`.
- Browser setup from product root: `npx --prefix mobilni-aplikace playwright install --with-deps chromium`. After building, run `npm run test:game:browser --prefix mobilni-aplikace`; other targeted package scripts include `test:invitation:browser`, `test:beta-admission:browser` and `test:owner:browser`.
- Backend Linux-x64 CI sequence from product root (requires an x64-capable executor):
  ```sh
  dotnet restore backend-tests/BackendTests.csproj -p:SelfContained=true --locked-mode
  dotnet build backend-tests/BackendTests.csproj -c Release -r linux-x64 -p:SelfContained=true --no-restore
  dotnet run --project backend-tests/BackendTests.csproj -c Release -r linux-x64 --no-build --no-restore
  ```
- Backend focused harness groups append, for example, `-- --identity` or `-- --mobile-game` to that run command; the complete matrix is in `.github/workflows/kufr-backend.yml`.
- From `firmware/`: `pio run` compiles the production image; it does not flash hardware. Simulator tests need a compiled native bridge; read `firmware/simulator/README.md` first.
- From private deploy root: `node --test scripts/ci/*.test.cjs`, `python3 -m unittest discover -s scripts/ci -p 'test_*.py'`, and `python3 -m unittest discover -s kufr/backend/deploy -p 'test_*.py'`.
- Workflow lint in private CI uses checksum-verified actionlint `1.7.12`: `"$RUNNER_TEMP/actionlint" -shellcheck= -pyflakes=`; installation/checksum is in `.github/workflows/ci.yml`. Dispatching its production-connected workflow is not equivalent to local lint.

## Observed conventions & pitfalls

- Vue screens use SFCs with `<script setup lang="ts">`; feature tests are colocated `*.spec.ts`. Vitest uses `happy-dom` and discovers specs in `tests/`, `src/features/`, `src/application/` and `src/platform/`. Node tests use `*.test.mjs` separately.
- UI copy is Czech (for example `GameConsent.vue`); platform capabilities stay separate from shared Vue screens.
- `backend-tests/BackendTests.csproj` is an executable harness, not a `dotnet test` project. CI's `linux-x64` runtime is explicit; do not assume it runs natively on ARM64 or rewrite committed NuGet locks just to accommodate a local platform.
- `contracts/generated/` is generator output; change schemas/generators and regenerate. The mobile build rejects stale validators. Do not edit `node_modules/`, `dist/` or `.pio/` as source.
- The `cleanup` branch removed files still linked by README, including app-root `AGENTS.md` and `PRODUCT.md`; verify paths before following historical links. This parent remains the workspace instruction file; do not recreate deleted product guidance without a task requiring it.
- Offline provider doubles and synthetic data do not establish production AI, physical hardware or native-phone acceptance. A successful web build is not a signing or deployment result.
