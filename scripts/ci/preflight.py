#!/usr/bin/env python3
"""Run the app repository's local pre-PR validation plan."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tempfile
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Sequence


ROOT = Path(__file__).resolve().parents[2]
BACKEND_GROUPS = (
    "--observability", "--push", "--push-http", "--twin", "--twin-media",
    "--identity", "--prelaunch-http-slow-artwork", "--prelaunch-workshop-http",
    "--persona-reliability", "--persona-lab", "--persona-lab-transport",
    "--mobile-game", "--mobile-game-http", "--owner-player-http",
    "--mobile-workshop-socket", "--mobile-workshop-live-http",
    "--mobile-game-boss", "--mobile-game-teams", "--mobile-game-progress",
    "--sealed-case-runtime", "--account-deletion",
)
GROUPS = ("repository", "design", "contracts", "frontend", "backend", "browser",
          "android", "ios", "firmware", "site", "coverage", "setup")


@dataclass(frozen=True)
class Check:
    name: str
    group: str
    platform: str
    command: tuple[str, ...]
    cwd: str = "."
    tools: tuple[str, ...] = ()
    reason: str = ""
    env: tuple[tuple[str, str], ...] = ()
    expected_version: str = ""
    required_files: tuple[str, ...] = ()


def host_platform() -> str:
    system = platform.system().lower()
    if system == "windows":
        return "windows"
    if system == "darwin":
        return "macos"
    if system == "linux":
        return "linux"
    return system


def _check(name: str, group: str, platform_name: str, command: Sequence[str],
           cwd: str = ".", tools: Sequence[str] = (), reason: str = "",
           env: Sequence[tuple[str, str]] = (), expected_version: str = "",
           required_files: Sequence[str] = ()) -> Check:
    return Check(name, group, platform_name, tuple(command), cwd, tuple(tools), reason,
                 tuple(env), expected_version, tuple(required_files))


def checkout_temp_dir(root: Path = ROOT) -> Path:
    checkout_key = hashlib.sha256(str(root.resolve()).encode("utf-8")).hexdigest()[:12]
    return Path(tempfile.gettempdir()) / f"dildovander-preflight-{checkout_key}"


def native_twin_path(root: Path = ROOT, host: str | None = None) -> Path:
    suffix = ".exe" if (host or host_platform()) == "windows" else ""
    return checkout_temp_dir(root) / f"native-twin{suffix}"


def media_twin_path(root: Path = ROOT, host: str | None = None) -> Path:
    suffix = ".exe" if (host or host_platform()) == "windows" else ""
    return checkout_temp_dir(root) / f"native-media-twin{suffix}"


def version_matches(expectation: str, reported: str) -> bool:
    policy, expected = expectation.split(":", 1)
    actual = reported.strip().removeprefix("v")
    if policy == "exact":
        return actual == expected
    if policy == "latest-patch":
        try:
            expected_parts = tuple(int(part) for part in expected.split("."))
            actual_parts = tuple(int(part) for part in actual.split("."))
        except ValueError:
            return False
        return len(expected_parts) == 3 and len(actual_parts) == 3 \
            and actual_parts[:2] == expected_parts[:2] and actual_parts[2] >= expected_parts[2]
    if policy == "xcode":
        return actual.splitlines()[0].strip() == f"Xcode {expected}" if actual else False
    return False


def report_is_current_and_complete(report: dict[str, object], identity: dict[str, str]) -> bool:
    return (all(report.get(key) == value for key, value in identity.items())
            and report.get("complete") is True
            and report.get("scope") in ("local", "all")
            and report.get("selection") == "scope"
            and (report.get("scope") != "all" or report.get("ci_coverage_complete") is True)
            and not report.get("missing_platform_coverage"))


def identity_changed(start: dict[str, str], end: dict[str, str]) -> bool:
    return start != end


def build_plan(scope: str, host: str, install_dependencies: bool = False,
               groups: Sequence[str] | None = None, architecture: str | None = None) -> list[Check]:
    """Return the complete command plan, without probing or changing the machine."""
    checks: list[Check] = []
    run_tag = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%f")
    py = "python" if host == "windows" else "python3"
    npm = "npm.cmd" if host == "windows" else "npm"
    node = "node.exe" if host == "windows" else "node"
    npm_prefix = [npm]
    if install_dependencies:
        for folder in ("contracts", "design-system", "mobilni-aplikace", "firmware/simulator"):
            checks.append(_check(f"Install {folder} locked dependencies", "setup", "portable",
                                 [*npm_prefix, "ci", "--prefix", folder], tools=[npm]))
        npx = "npx.cmd" if host == "windows" else "npx"
        checks.append(_check("Install Playwright Chromium", "setup", "portable",
                             [npx, "--prefix", "mobilni-aplikace", "playwright", "install", "chromium"], tools=[npx]))

    def add(name: str, group: str, platform_name: str, command: Sequence[str],
            cwd: str = ".", tools: Sequence[str] = (), reason: str = "",
            env: Sequence[tuple[str, str]] = (), expected_version: str = "",
            required_files: Sequence[str] = ()) -> None:
        checks.append(_check(name, group, platform_name, command, cwd, tools, reason,
                             env, expected_version, required_files))

    node_version = (ROOT / "contracts/.nvmrc").read_text(encoding="utf-8").strip()
    sdk_config = json.loads((ROOT / "global.json").read_text(encoding="utf-8"))["sdk"]
    dotnet_pin = sdk_config["version"]
    dotnet_policy = "latest-patch" if sdk_config.get("rollForward") == "latestPatch" else "exact"
    add("Pinned Node.js version", "repository", "portable", [node, "--version"], tools=[node],
        expected_version=f"exact:{node_version}")
    add("global.json .NET SDK version", "repository", "portable", ["dotnet", "--version"], tools=["dotnet"],
        expected_version=f"{dotnet_policy}:{dotnet_pin}")

    add("Workflow contract tests", "repository", "portable", [node, "--test", "scripts/ci/*.test.cjs"], tools=[node])
    add("Whitespace and conflict-marker check", "repository", "portable", ["git", "diff", "--check", "HEAD"], tools=["git"])
    add("CI Python helper tests", "repository", "portable", [py, "-m", "unittest", "discover", "-s", "scripts/ci", "-p", "test_*.py"], tools=[py])
    add("Workflow actionlint validation", "repository", "portable", ["actionlint", "-shellcheck=", "-pyflakes="], tools=["actionlint"])
    add("Secret scan requires complete local history", "repository", "portable",
        ["git", "rev-parse", "--is-shallow-repository"], tools=["git"], expected_version="exact:false")
    add("Gitleaks scanner regression tests", "repository", "portable",
        [py, "-m", "unittest", "discover", "-s", "scripts/ci", "-p", "test_secret_scan.py"],
        tools=[py, "gitleaks"], env=(("GITLEAKS_BINARY", "$tool:gitleaks"),))
    add("Gitleaks checkout history scan", "repository", "portable",
        ["gitleaks", "git", "--config=.gitleaks.toml", "--redact=100", "--no-banner", "--log-opts=HEAD"],
        tools=["gitleaks"])
    add("Fast model build tests", "repository", "portable", [py, "-m", "unittest", "discover", "-s", "mechanika/final", "-p", "test_fast_build.py"], tools=[py])
    add("CAD host tests", "repository", "portable", [py, "-m", "unittest", "discover", "-s", "web/deploy", "-p", "test_cad_host.py"], tools=[py])
    add("Design token source check", "design", "portable", [py, "scripts/generate_design_tokens.py", "--check"], tools=[py])
    add("Design system tests", "design", "portable", [npm, "test", "--prefix", "design-system"], tools=[npm])
    add("Design system Python tests", "design", "portable", [py, "-m", "unittest", "discover", "-s", "scripts", "-p", "test_design_system.py"], tools=[py])
    add("Design system integrity", "design", "portable", [py, "scripts/check_design_system.py"], tools=[py])
    add("Generated effects and tests", "design", "portable", [py, "mechanika/final/generate_effects.py", "--check"], tools=[py])
    add("Generated effects tests", "design", "portable", [py, "-m", "unittest", "discover", "-s", "mechanika/final", "-p", "test_effects.py"], tools=[py])
    add("Contracts test suite", "contracts", "portable", [npm, "test", "--prefix", "contracts"], tools=[npm])

    add("Frontend Node and Vitest tests", "frontend", "portable", [npm, "test", "--prefix", "mobilni-aplikace"], tools=[npm])
    add("Frontend typecheck", "frontend", "portable", [npm, "run", "typecheck", "--prefix", "mobilni-aplikace"], tools=[npm])
    add("Frontend production build and generated contract checks", "frontend", "portable", [npm, "run", "build", "--prefix", "mobilni-aplikace"], tools=[npm])

    add("AI evaluation pure Python tests", "repository", "portable",
        [py, "-m", "unittest", "discover", "-s", "ai-eval", "-p", "test_*.py"], tools=[py])
    add("Site application/viewer boundary", "site", "portable", [py, "scripts/ci/ui_boundary.py"], tools=[py])
    site_python_tests = (
        ("Web", "web", "test_*.py"), ("Web deploy", "web/deploy", "test_*.py"),
        ("Viewer style", "mechanika/final", "test_viewer_style.py"),
        ("Viewer document", "mechanika/final", "test_viewer_document.py"),
        ("Twin manifest", "mechanika/final", "test_twin_manifest.py"),
        ("Effects", "mechanika/final", "test_effects.py"),
        ("Model bundle export", "mechanika/final", "test_model_bundle_export.py"),
        ("Model verification", "mechanika/final", "test_model_verification.py"),
    )
    for label, directory, pattern in site_python_tests:
        add(f"Site fixtures: {label}", "site", "portable",
            [py, "-m", "unittest", "discover", "-s", directory, "-p", pattern], tools=[py])
    add("Site viewer Node fixtures", "site", "portable",
        [node, "--test", "mechanika/final/test_viewer.cjs", "mechanika/final/test_twin_viewer.cjs",
         "mechanika/final/test_twin_media.cjs", "mechanika/final/test_show_timeline.cjs"], tools=[node])
    add("Site simulator dashboard validation", "site", "portable",
        [node, "firmware/simulator/verify-dashboard.mjs"], tools=[node])

    machine = (architecture or platform.machine()).lower()
    x64 = machine in ("x86_64", "amd64", "x64")
    rid = "linux-x64" if host == "linux" and x64 else "win-x64" if host == "windows" and x64 else None
    native_host = native_twin_path(host=host)
    add("Build simulator native test host", "backend", "portable",
        [py, "firmware/simulator/native/build_host.py", "--output",
         str(native_host)], tools=[py, "cxx"])
    restore = ["dotnet", "restore", "backend-tests/BackendTests.csproj"]
    if rid:
        restore.append("-p:SelfContained=true")
    restore.append("--locked-mode")
    add("Restore backend executable harness", "backend", "portable", restore, tools=["dotnet"])
    build = ["dotnet", "build", "backend-tests/BackendTests.csproj", "-c", "Release"]
    if rid:
        build.extend(["-r", rid, "-p:SelfContained=true"])
    build.append("--no-restore")
    add("Build backend executable harness", "backend", "portable", build, tools=["dotnet"])
    harness_env = (("KUFR_TWIN_TEST_NATIVE", str(native_host)),)
    for group in ("", *BACKEND_GROUPS):
        label = "default" if not group else group.removeprefix("--")
        command = ["dotnet", "run", "--project", "backend-tests/BackendTests.csproj", "-c", "Release"]
        if rid:
            command.extend(["-r", rid])
        command.extend(["--no-build", "--no-restore"])
        if group:
            command.extend(["--", group])
        tools = ["dotnet", "cxx"] if group in ("--twin", "--twin-media") else ["dotnet"]
        add(f"Backend harness: {label}", "backend", "portable", command, tools=tools, env=harness_env)

    backend_dll = ROOT / "backend/bin/Release/net10.0" / rid / "backend.dll" if rid else ROOT / "backend/bin/Release/net10.0/backend.dll"
    test_dll = ROOT / "backend-tests/bin/Release/net10.0" / rid / "BackendTests.dll" if rid else ROOT / "backend-tests/bin/Release/net10.0/BackendTests.dll"
    publish_dir = checkout_temp_dir() / f"backend-publish-{run_tag}"
    published_backend = publish_dir / "backend.dll"
    publish = ["dotnet", "publish", "backend/backend.csproj", "-c", "Release"]
    if rid:
        publish.extend(["-r", rid, "--self-contained", "true"])
    publish.extend(["--no-build", "--no-restore", "-o", str(publish_dir)])
    add("Publish local backend smoke binary", "backend", "portable", publish, tools=["dotnet"])
    add("Backend lifecycle and SQLite diagnostics tests", "backend", "portable",
        [node, "--test", "backend/tests/child-process-lifecycle.test.mjs",
         "backend/tests/sqlite-startup-diagnostic.test.mjs"], tools=[node])
    add("Backend HTTP smoke", "backend", "portable", [node, "backend/tests/smoke.mjs"], tools=[node],
        env=(("KUFR_BACKEND_DLL", str(published_backend)),), required_files=(str(published_backend),))
    add("Keepsake HTTP integration", "backend", "portable", [node, "backend/tests/keepsake-http.mjs"], tools=[node],
        env=(("KUFR_BACKEND_DLL", str(published_backend)), ("KUFR_TESTS_DLL", str(test_dll))),
        required_files=(str(published_backend), str(test_dll)))
    add("Backend Python client tests", "backend", "portable",
        [py, "-m", "unittest", "discover", "-s", "backend/tests", "-p", "test_*.py"], tools=[py])
    add("Voice provisioning script tests", "backend", "portable",
        [py, "-m", "unittest", "discover", "-s", "scripts", "-p", "test_provision_dv01_voice.py"], tools=[py])
    add("Backend deployment configuration tests", "backend", "portable",
        [py, "-m", "unittest", "discover", "-s", "backend/deploy", "-p", "test_*.py"], tools=[py])
    add("Backend twin release tests", "backend", "portable",
        [py, "-m", "unittest", "discover", "-s", "backend/deploy/twin", "-p", "test_twin_release.py"], tools=[py])
    harness_output = ROOT / "backend-tests/bin/Release/net10.0" / rid if rid else ROOT / "backend-tests/bin/Release/net10.0"
    add("Backend harness diagnostic package provenance", "backend", "portable",
        [py, "scripts/ci/package.py", "pack", str(harness_output), str(checkout_temp_dir() / f"diagnostics-package-{run_tag}")],
        tools=[py], required_files=(str(test_dll),))
    add("Backend publish package provenance", "backend", "portable",
        [py, "scripts/ci/package.py", "pack", str(publish_dir), str(checkout_temp_dir() / f"backend-package-{run_tag}")],
        tools=[py], required_files=(str(published_backend),))

    # Browser journeys used by the site CI job. Browser installation is an explicit
    # dependency-install action; these commands never hide browser availability.
    browser_commands = (
        ("Shared application", "test:browser"), ("Launch", "test:launch:browser"),
        ("Owner flows", "test:owner:browser"), ("Persona lab", "test:persona-lab:browser"),
        ("Application updates", "test:updates:browser"),
    )
    for label, script in browser_commands:
        add(f"Browser journey: {label}", "browser", "portable", [npm, "run", script, "--prefix", "mobilni-aplikace"],
            tools=[npm], required_files=("mobilni-aplikace/dist/index.html",))
    for label, script in (("Debug", "tests/debug.browser.mjs"), ("Director", "tests/director.browser.mjs"),
                          ("Archive show", "scripts/archive-show.browser.mjs"),
                          ("Personal wand duel", "tests/personal-wand-duel.browser.mjs"),
                          ("Workshop offline", "tests/workshop-offline.browser.mjs"),
                          ("Workshop media", "tests/workshop-media.browser.mjs")):
        add(f"Browser journey: {label}", "browser", "portable", [node, script], "mobilni-aplikace", [node],
            required_files=("mobilni-aplikace/dist/index.html",))
    add("Account fixture browser journey", "browser", "portable", [npm, "run", "test:accounts:fixture", "--prefix", "mobilni-aplikace"],
        tools=[npm], env=(("KUFR_TESTS_DLL", str(test_dll)),),
        required_files=(str(test_dll), "mobilni-aplikace/dist/index.html"))
    add("Game browser journey", "browser", "portable", [npm, "run", "test:game:browser", "--prefix", "mobilni-aplikace"],
        tools=[npm], required_files=("mobilni-aplikace/dist/index.html",))
    web_dist = ROOT / "mobilni-aplikace/dist"
    add("Compiled site input tests", "site", "portable",
        [py, "-m", "unittest", "discover", "-s", "web", "-p", "test_site_inputs.py"], tools=[py],
        env=(("KUFR_SITE_WEB_DIST", str(web_dist)),), required_files=(str(web_dist / "index.html"),))
    site_source = checkout_temp_dir() / f"site-source-{run_tag}"
    site_package = checkout_temp_dir() / f"site-package-{run_tag}"
    add("Assemble local site package", "site", "portable",
        [py, "web/build-site.py", "--destination", str(site_source), "--web-dist", str(web_dist)],
        tools=[py], required_files=(str(web_dist / "index.html"),))
    add("Package local site with provenance", "site", "portable",
        [py, "scripts/ci/package.py", "pack", str(site_source), str(site_package)], tools=[py],
        required_files=(str(site_source),))
    add("Package compiled web build with provenance", "site", "portable",
        [py, "scripts/ci/package.py", "pack", str(web_dist), str(checkout_temp_dir() / f"web-package-{run_tag}")],
        tools=[py], required_files=(str(web_dist / "index.html"),))

    # Native platform checks mirror their workflow's test/build entry points.
    for script in ("test_android.py",):
        add("Android helper tests", "android", "portable", [py, "-m", "unittest", "discover", "-s", "mobilni-aplikace/scripts", "-p", script], tools=[py])
    add("Android debug build", "android", "portable", [py, "mobilni-aplikace/scripts/android.py", "debug"], tools=[py, "java21", "android-sdk"])
    add("iOS IPA build script syntax", "ios", "portable", ["bash", "-n", "scripts/build-ios-ipa.sh"],
        cwd="mobilni-aplikace", tools=["bash"])
    add("Web publish helper tests", "frontend", "portable",
        [py, "-m", "unittest", "discover", "-s", "mobilni-aplikace/scripts", "-p", "test_publish_*.py"], tools=[py])
    for script in ("test_configure_ios_signing.py", "test_validate_ios_profile.py", "test_ios_native_preparation.py", "test_ios_home_render.py"):
        add(f"iOS helper tests: {script}", "ios", "macos", [py, "-m", "unittest", "discover", "-s", "mobilni-aplikace/scripts", "-p", script], tools=[py])
    add("Pinned Xcode version", "ios", "macos", ["xcodebuild", "-version"], tools=["xcodebuild"],
        expected_version="xcode:26.3")
    npx = "npx.cmd" if host == "windows" else "npx"
    add("Sync iOS Capacitor project", "ios", "macos", [npx, "cap", "sync", "ios"],
        cwd="mobilni-aplikace", tools=[npx])
    add("Generate original iOS native assets", "ios", "macos", [node, "scripts/generate-native-assets.mjs"],
        cwd="mobilni-aplikace", tools=[node])
    add("Resolve iOS Swift packages", "ios", "macos",
        ["xcodebuild", "-resolvePackageDependencies", "-project", "ios/App/App.xcodeproj", "-scheme", "App"],
        cwd="mobilni-aplikace", tools=["xcodebuild"])
    for script in ("test-native-transport.sh", "test-native-debug.sh", "test-native-provisioning.sh", "test-native-identity.sh", "test-native-diagnostic-export.sh"):
        add(f"iOS native test: {script}", "ios", "macos", ["bash", f"scripts/{script}"],
            cwd="mobilni-aplikace", tools=["bash", "xcodebuild"])
    add("iOS unsigned simulator build", "ios", "macos",
        ["xcodebuild", "-project", "ios/App/App.xcodeproj", "-scheme", "App", "-configuration", "Debug",
         "-destination", "generic/platform=iOS Simulator", "-derivedDataPath",
         str(checkout_temp_dir() / "ios-derived-data"), "CODE_SIGNING_ALLOWED=NO", "build"],
        cwd="mobilni-aplikace", tools=["xcodebuild"])

    firmware_commands = (
        ("Firmware CI tests", [py, "-m", "unittest", "discover", "-s", "firmware/ci", "-p", "test_*.py"], [py]),
        ("Servo bench tests", [py, "firmware/diagnostics/servo-bench/tests/run_tests.py"], [py]),
        ("Servo panel Node tests", [node, "--test", "firmware/diagnostics/servo-bench/web/tests/*.test.mjs"], [node]),
        ("Servo panel Python tests", [py, "-B", "-m", "unittest", "discover", "-s", "firmware/diagnostics/servo-bench/web/tests", "-p", "test_*.py", "-v"], [py]),
        ("Firmware version and OTA tests", [py, "-m", "unittest", "discover", "-s", "firmware/tools", "-p", "test_*.py"], [py]),
        ("Hardware service diagnostic tests", [py, "-m", "unittest", "discover", "-s", "firmware/diagnostics/hardware-service", "-p", "test_*.py"], [py]),
        ("Network service diagnostic tests", [py, "-m", "unittest", "discover", "-s", "firmware/diagnostics/network-service", "-p", "test_*.py"], [py]),
        ("Firmware show tests", [py, "firmware/tests/run_show_tests.py"], [py]),
        ("Firmware host simulator tests", [py, "firmware/simulator/native/test_host.py"], [py]),
        ("Firmware native simulator build", [py, "firmware/simulator/native/build_host.py", "--output",
                                              str(media_twin_path(host=host))], [py, "cxx"]),
        ("Firmware simulator media tests", [py, "firmware/simulator/native/test_media.py", "--executable",
                                             str(media_twin_path(host=host))], [py]),
        ("Legacy show regression comparison", [py, "firmware/tests/compare_legacy_show.py"], [py]),
        ("DV02 model checks", [py, "scripts/check_dv02.py"], [py]),
        ("Show timeline Node tests", [node, "--test", "mechanika/final/test_show_timeline.cjs"], [node]),
        ("Firmware dance tests", ["bash", "tests/run_dance_tests.sh"], ["bash"], "firmware"),
        ("Firmware image build", ["pio", "run", "-d", "firmware", "-e", "production", "-s"], ["pio"]),
        ("Firmware OTA image gate", [py, "firmware/tools/ota_release.py", "check", "--binary",
                                      "firmware/.pio/build/production/firmware.bin"], [py]),
        ("Firmware board memory diagnostic build", ["pio", "run", "-d", "firmware", "-e", "diag-board-memory", "-s"], ["pio"]),
        ("Firmware PCA9685 diagnostic build", ["pio", "run", "-d", "firmware", "-e", "diag-pca9685-logic", "-s"], ["pio"]),
        ("Firmware servo bench diagnostic build", ["pio", "run", "-d", "firmware", "-e", "diag-servo-bench", "-s"], ["pio"]),
        ("Prepare hardware service diagnostic example", [py, "diagnostics/hardware-service/make_config.py",
                                                          "diagnostics/hardware-service/service_config.example.json"],
         [py], "firmware"),
        ("Firmware hardware service diagnostic build", ["pio", "run", "-d", "firmware", "-e", "diag-hardware-service", "-s"], ["pio"]),
        ("Firmware network service diagnostic build", ["pio", "run", "-d", "firmware", "-e", "diag-network-service", "-s"], ["pio"]),
    )
    for item in firmware_commands:
        name, command, tools, *working_directory = item
        platform_name = "portable"
        if name in ("Servo bench tests", "Firmware show tests", "Firmware host simulator tests",
                    "Firmware native simulator build", "Legacy show regression comparison", "DV02 model checks"):
            tools = [*tools, "cxx"]
        if name == "Firmware dance tests":
            tools = [*tools, "g++"]
        if name == "Firmware simulator media tests":
            working_directory = ["."]
        add(name, "firmware", platform_name, command,
            cwd=working_directory[0] if working_directory else ".", tools=tools,
            required_files=(str(media_twin_path(host=host)),) if name == "Firmware simulator media tests" else ())
    if scope == "all":
        add("Linux firmware inline C++ host workflow coverage", "coverage", "linux", [],
            tools=["coverage-gap:Linux firmware workflow compiles additional C++ harnesses inline; they are not represented by individual runner checks"],
            reason="CI has additional inline GNU C++ harnesses beyond the portable firmware checks planned above")
    if scope == "all":
        add("Linux x64 CI host coverage", "coverage", "linux",
            [py, "-c", "import platform; assert platform.system() == 'Linux' and platform.machine().lower() in ('x86_64', 'amd64')"], tools=[py])

    if scope == "local":
        checks = [check for check in checks if check.platform in ("portable", host)]
    if groups:
        selected_groups = set(groups)
        if install_dependencies:
            selected_groups.add("setup")
        checks = [check for check in checks if check.group in selected_groups]
    return checks


def tree_fingerprint(root: Path, runner: Callable[..., subprocess.CompletedProcess] = subprocess.run) -> dict[str, str]:
    def git(*args: str) -> str:
        result = runner(["git", *args], cwd=root, capture_output=True, text=True, check=True)
        return result.stdout.strip()

    status = git("status", "--porcelain=v1", "--untracked-files=all")
    diff = git("diff", "--binary", "HEAD")
    untracked_paths = git("ls-files", "--others", "--exclude-standard", "-z")
    status_digest = hashlib.sha256()
    status_digest.update(status.encode("utf-8"))
    status_digest.update(diff.encode("utf-8"))
    for path in untracked_paths.split("\0"):
        if path:
            relative = Path(path)
            status_digest.update(path.encode("utf-8", errors="surrogateescape"))
            status_digest.update((root / relative).read_bytes())
    return {
        "head": git("rev-parse", "HEAD"),
        "tree": git("rev-parse", "HEAD^{tree}"),
        "worktree_status_sha256": status_digest.hexdigest(),
    }


def execute_plan(checks: Sequence[Check], root: Path, runner: Callable[..., subprocess.CompletedProcess],
                 available: Callable[[str], str | None] = shutil.which,
                 host: str | None = None,
                 progress: Callable[[str], None] | None = print) -> tuple[list[dict[str, object]], list[dict[str, str]], bool]:
    results: list[dict[str, object]] = []
    missing: list[dict[str, str]] = []
    failed = False
    for index, check in enumerate(checks, start=1):
        if check.platform in ("linux", "macos") and host and check.platform != host:
            missing.append({"check": check.name, "tool": f"{check.platform} host", "platform": check.platform})
            results.append({**asdict(check), "command": list(check.command), "status": "missing",
                            "missing_tools": [f"{check.platform} host"]})
            if progress:
                progress(f"[{index}/{len(checks)}] {check.name}: missing ({check.platform} host required)")
            continue

        def tool_available(tool: str) -> str | None:
            if tool == "android-sdk":
                sdk_root = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
                if not sdk_root and os.name == "nt" and os.environ.get("LOCALAPPDATA"):
                    sdk_root = str(Path(os.environ["LOCALAPPDATA"]) / "Android/Sdk")
                if not sdk_root:
                    return None
                sdk = Path(sdk_root)
                required = (sdk / "platforms/android-36/android.jar",
                            sdk / "build-tools/36.0.0")
                return str(sdk) if sdk.is_dir() and all(path.exists() for path in required) else None
            if tool == "java21":
                java_home = os.environ.get("JAVA_HOME")
                java_path = (Path(java_home) / "bin" / ("java.exe" if os.name == "nt" else "java")) if java_home else None
                java = str(java_path) if java_path and java_path.is_file() else available("java")
                if not java:
                    return None
                version = subprocess.run([java, "-version"], capture_output=True, text=True, check=False)
                return java if re.search(r'version "21\.', version.stdout + version.stderr) else None
            if tool == "cxx":
                compiler = available("g++") or available("cl")
                if compiler:
                    return compiler
                if host == "windows":
                    vswhere = Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) / "Microsoft Visual Studio/Installer/vswhere.exe"
                    if vswhere.is_file():
                        result = subprocess.run(
                            [str(vswhere), "-latest", "-products", "*", "-requires",
                             "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
                            capture_output=True, text=True, check=False)
                        if result.returncode == 0 and result.stdout.strip():
                            setup = Path(result.stdout.strip()) / "Common7/Tools/VsDevCmd.bat"
                            if setup.is_file():
                                return str(setup)
                return None
            return available(tool)

        missing_files = [path for path in check.required_files if not (root / path).exists()]
        unavailable = [tool for tool in check.tools if tool_available(tool) is None]
        unavailable.extend(f"file:{path}" for path in missing_files)
        if unavailable:
            for tool in unavailable:
                missing.append({"check": check.name, "tool": tool, "platform": check.platform})
            results.append({**asdict(check), "command": list(check.command), "status": "missing", "missing_tools": unavailable})
            if progress:
                progress(f"[{index}/{len(checks)}] {check.name}: missing ({', '.join(unavailable)})")
            continue
        try:
            environment = os.environ.copy()
            for key, value in check.env:
                if value.startswith("$tool:"):
                    value = tool_available(value.removeprefix("$tool:")) or value
                environment[key] = value
            run_options: dict[str, object] = {"cwd": root / check.cwd, "env": environment, "check": False}
            if check.expected_version:
                run_options.update({"capture_output": True, "text": True})
            completed = runner(list(check.command), **run_options)
            version_output = "\n".join(part for part in (completed.stdout or "", completed.stderr or "") if part)
            observed_version = version_output.strip() if check.expected_version else ""
            version_ok = not check.expected_version or version_matches(check.expected_version, observed_version)
            status = "passed" if completed.returncode == 0 and version_ok else "failed"
            failed = failed or status == "failed"
            results.append({**asdict(check), "command": list(check.command), "status": status,
                            "exit_code": completed.returncode,
                            **({"observed_version": observed_version} if check.expected_version else {})})
            if progress:
                detail = f"{observed_version.splitlines()[0] if observed_version else '<no version>'}; expected {check.expected_version}" if check.expected_version else f"exit {completed.returncode}"
                progress(f"[{index}/{len(checks)}] {check.name}: {status} ({detail})")
        except OSError as error:
            failed = True
            results.append({**asdict(check), "command": list(check.command), "status": "failed",
                            "error": str(error)})
            if progress:
                progress(f"[{index}/{len(checks)}] {check.name}: failed ({error})")
    return results, missing, failed


def default_report_path() -> Path:
    return checkout_temp_dir() / "latest-report.json"


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scope", choices=("local", "all"), default="local",
                        help="local runs portable checks and this host's native checks; all requires every platform group")
    parser.add_argument("--list", action="store_true", help="print planned commands without executing them")
    parser.add_argument("--dry-run", action="store_true", help="alias for --list")
    parser.add_argument("--group", action="append", choices=GROUPS,
                        help="run only this check group; repeat for multiple groups (local scope only)")
    parser.add_argument("--install-dependencies", action="store_true",
                        help="explicitly run npm ci for each checked-in Node project before validation")
    parser.add_argument("--report", nargs="?", type=Path, const=default_report_path(),
                        help="write a JSON report (default: a checkout-specific file under the system temp directory)")
    parser.add_argument("--check-report", type=Path, help="check a prior report for a changed HEAD or worktree")
    parser.add_argument("--host", choices=("windows", "linux", "macos"), default=host_platform(),
                        help=argparse.SUPPRESS)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    if args.scope == "all" and args.group:
        print("--group is available with --scope local; --scope all always retains strict full coverage.", file=sys.stderr)
        return 2
    if args.report:
        args.report = args.report.resolve()
        if args.report.is_relative_to(ROOT.resolve()):
            print("Report path must be outside the checkout; use the default temp path or another external path.", file=sys.stderr)
            return 2
    try:
        identity = tree_fingerprint(ROOT)
    except (OSError, subprocess.CalledProcessError) as error:
        print(f"Cannot identify Git checkout: {error}", file=sys.stderr)
        return 2
    if args.check_report:
        try:
            report = json.loads(args.check_report.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            print(f"Cannot read report: {error}", file=sys.stderr)
            return 2
        current_and_complete = report_is_current_and_complete(report, identity)
        if args.scope == "all":
            current_and_complete = current_and_complete and report.get("ci_coverage_complete") is True
        print(f"current and complete {report.get('scope')} coverage" if current_and_complete else "stale, partial, or incomplete")
        return 0 if current_and_complete else 1

    checks = build_plan(args.scope, args.host, args.install_dependencies, args.group)
    if args.list or args.dry_run:
        print(json.dumps({"scope": args.scope, "host": args.host,
                          "groups": sorted({check.group for check in checks}),
                          "checks": [{**asdict(check), "command": list(check.command)} for check in checks]}, indent=2))
        return 0

    results, missing, failed = execute_plan(checks, ROOT, subprocess.run, host=args.host)
    try:
        final_identity = tree_fingerprint(ROOT)
    except (OSError, subprocess.CalledProcessError) as error:
        print(f"Cannot identify final Git checkout state: {error}", file=sys.stderr)
        return 2
    source_changed = identity_changed(identity, final_identity)
    if source_changed:
        failed = True
        results.append({"name": "Source tree remained unchanged during checks", "group": "integrity",
                        "platform": "portable", "command": [], "status": "failed",
                        "reason": "Git HEAD, tree, or worktree contents changed while checks were running"})
    report = {**final_identity, "starting_identity": identity, "ending_identity": final_identity,
              "scope": args.scope, "host": args.host, "architecture": platform.machine(),
              "groups": sorted({check.group for check in checks}),
              "selection": "group-filtered" if args.group else "scope",
              "report_path": str(args.report) if args.report else None,
              "created_at": datetime.now(timezone.utc).isoformat(),
              "checks": results, "missing_platform_coverage": missing,
              "source_changed_during_checks": source_changed,
              "complete": not failed and not missing and not source_changed,
              "ci_coverage_complete": args.scope == "all" and not failed and not missing and not source_changed}
    print(json.dumps(report, indent=2))
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + os.linesep, encoding="utf-8")
    return 1 if failed or missing else 0


if __name__ == "__main__":
    raise SystemExit(main())
