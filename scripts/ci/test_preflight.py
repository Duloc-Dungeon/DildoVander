import subprocess
import tempfile
import unittest
from pathlib import Path

import preflight


class PreflightPlanTests(unittest.TestCase):
    def test_local_plan_contains_pr_checks_but_only_host_native_checks(self):
        checks = preflight.build_plan("local", "windows")
        names = {check.name for check in checks}

        self.assertIn("Contracts test suite", names)
        self.assertIn("Frontend Node and Vitest tests", names)
        self.assertIn("Backend harness: default", names)
        self.assertIn("Android debug build", names)
        self.assertNotIn("iOS simulator build", names)
        self.assertNotIn("Firmware image build", names)
        self.assertNotIn("CAD host tests", names)
        self.assertNotIn("Generated effects and tests", names)
        self.assertNotIn("Build simulator native test host", names)
        self.assertNotIn("Stage backend test simulator", names)
        self.assertTrue(all(check.platform in ("portable", "windows") for check in checks))

    def test_all_plan_keeps_nonlocal_release_groups_visible(self):
        checks = preflight.build_plan("all", "windows")
        names = {check.name for check in checks}

        self.assertIn("iOS unsigned simulator build", names)
        self.assertNotIn("Firmware image build", names)
        self.assertIn("Android debug build", names)
        self.assertTrue(any(check.group == "browser" for check in checks))
        repository_checks = [check for check in checks if check.group == "repository"]
        self.assertTrue(any(check.name == "Workflow actionlint validation" for check in repository_checks))
        self.assertTrue(any(check.name == "Gitleaks checkout history scan" for check in repository_checks))
        self.assertFalse(any("firmware" in check.name.lower() or "cad" in check.name.lower() for check in checks))

    def test_dependency_install_commands_only_appear_when_explicit(self):
        default = preflight.build_plan("local", "linux")
        installed = preflight.build_plan("local", "linux", install_dependencies=True)

        self.assertFalse(any(check.group == "setup" for check in default))
        installs = [check for check in installed if check.group == "setup"]
        self.assertEqual(4, len(installs))
        npm_installs = [check for check in installs if check.name.startswith("Install ") and "Playwright" not in check.name]
        self.assertTrue(all(check.command[1] == "ci" for check in npm_installs))
        self.assertTrue(any("playwright" in check.command for check in installs))


class PreflightExecutionTests(unittest.TestCase):
    def test_missing_tools_are_reported_without_calling_subprocess(self):
        check = preflight.Check("optional tool", "backend", "portable", ("compiler",), tools=("compiler",))
        calls = []

        results, missing, failed = preflight.execute_plan(
            [check], Path("."), lambda *args, **kwargs: calls.append(args), lambda _: None
        )

        self.assertEqual([], calls)
        self.assertFalse(failed)
        self.assertEqual("missing", results[0]["status"])
        self.assertEqual("compiler", missing[0]["tool"])

    def test_nonzero_exit_is_not_hidden(self):
        check = preflight.Check("contracts", "contracts", "portable", ("npm", "test"), tools=("npm",))
        runner_calls = []

        def runner(command, **kwargs):
            runner_calls.append((command, kwargs))
            return subprocess.CompletedProcess(command, 7)

        results, missing, failed = preflight.execute_plan(
            [check], Path("/repo"), runner, lambda _: "npm"
        )

        self.assertTrue(failed)
        self.assertEqual([], missing)
        self.assertEqual("failed", results[0]["status"])
        self.assertEqual(7, results[0]["exit_code"])
        self.assertEqual(Path("/repo"), runner_calls[0][1]["cwd"])

    def test_backend_plan_uses_locked_runtime_ids_without_simulator_prerequisites(self):
        windows = preflight.build_plan("local", "windows", architecture="AMD64")
        mac = preflight.build_plan("local", "macos", architecture="arm64")
        windows_build = next(check for check in windows if check.name == "Build backend executable harness")
        mac_build = next(check for check in mac if check.name == "Build backend executable harness")
        default_harness = next(check for check in windows if check.name == "Backend harness: default")
        names = {check.name for check in windows}

        self.assertIn("win-x64", windows_build.command)
        self.assertNotIn("osx-arm64", mac_build.command)
        self.assertNotIn("-r", mac_build.command)
        self.assertIn("Backend harness: default", names)
        self.assertNotIn("Backend harness: twin", names)
        self.assertNotIn("Backend harness: twin-media", names)
        self.assertFalse(any("native test host" in check.name.lower() or "stage backend test simulator" in check.name.lower()
                             for check in windows))
        self.assertFalse(any("cxx" in check.tools for check in windows))
        self.assertFalse(any("KUFR_TWIN_TEST_NATIVE" in dict(check.env) for check in windows))
        expected_dll = str(preflight.ROOT / "backend-tests/bin/Release/net10.0/win-x64/BackendTests.dll")
        self.assertIn(expected_dll, default_harness.required_files)
        self.assertNotIn("--twin", preflight.BACKEND_GROUPS)
        self.assertNotIn("--twin-media", preflight.BACKEND_GROUPS)

    def test_account_fixture_receives_built_release_harness_and_missing_outputs_are_reported(self):
        checks = preflight.build_plan("local", "windows", architecture="AMD64")
        account = next(check for check in checks if check.name == "Account fixture browser journey")
        build = next(check for check in checks if check.name == "Build backend executable harness")

        expected_dll = str(preflight.ROOT / "backend-tests/bin/Release/net10.0/win-x64/BackendTests.dll")
        self.assertEqual(expected_dll, dict(account.env)["KUFR_TESTS_DLL"])
        self.assertIn(expected_dll, account.required_files)
        self.assertLess(checks.index(build), checks.index(account))
        keepsake = next(check for check in checks if check.name == "Keepsake HTTP integration")
        self.assertNotIn("KUFR_TWIN_TEST_NATIVE", dict(keepsake.env))

        missing_check = preflight.Check("fixture", "browser", "portable", ("node", "fixture"),
                                        required_files=("missing/BackendTests.dll",))
        calls = []
        results, missing, _ = preflight.execute_plan(
            [missing_check], Path("."), lambda *args, **kwargs: calls.append(args), lambda _: "node", progress=None
        )
        self.assertEqual([], calls)
        self.assertEqual("missing", results[0]["status"])
        self.assertEqual("file:missing/BackendTests.dll", missing[0]["tool"])

    def test_site_ci_and_pure_ai_eval_checks_are_planned(self):
        checks = preflight.build_plan("local", "windows")
        names = {check.name for check in checks}

        self.assertIn("AI evaluation pure Python tests", names)
        self.assertIn("Compiled site input tests", names)
        self.assertIn("Assemble local site package", names)
        self.assertIn("Package local site with provenance", names)
        self.assertIn("Site fixtures: Web application packaging and inputs", names)
        self.assertIn("Site fixtures: Web deploy test_application_routes.py", names)
        self.assertNotIn("Site fixtures: Web deploy test_cad_host.py", names)

    def test_default_plans_exclude_hardware_cad_and_native_simulator_checks(self):
        arm_local = preflight.build_plan("local", "linux", architecture="aarch64")
        all_checks = preflight.build_plan("all", "linux", architecture="x86_64")
        for checks in (arm_local, all_checks):
            names = {check.name for check in checks}
            self.assertNotIn("Build simulator native test host", names)
            self.assertNotIn("Firmware image build", names)
            self.assertNotIn("Firmware CI tests", names)
            self.assertNotIn("Stage backend test simulator", names)
            self.assertNotIn("Backend harness: twin", names)
            self.assertNotIn("Backend harness: twin-media", names)
            self.assertFalse(any("firmware/simulator/native" in " ".join(check.command) for check in checks))
            self.assertFalse(any("CAD" in check.name or "effects" in check.name.lower() or "model" in check.name.lower()
                                 for check in checks))
            self.assertFalse(any("pio" in tool.lower() for check in checks for tool in check.tools))
        self.assertNotIn("Linux x64 CI host coverage", {check.name for check in arm_local})

    def test_mac_native_commands_use_mobile_root_and_unsigned_simulator_build(self):
        checks = preflight.build_plan("local", "macos", architecture="arm64")
        native = next(check for check in checks if check.name == "iOS native test: test-native-transport.sh")
        sync = next(check for check in checks if check.name == "Sync iOS Capacitor project")
        build = next(check for check in checks if check.name == "iOS unsigned simulator build")
        version = next(check for check in checks if check.name == "Pinned Xcode version")

        self.assertEqual("mobilni-aplikace", native.cwd)
        self.assertEqual(("bash", "scripts/test-native-transport.sh"), native.command)
        self.assertEqual(("npx", "cap", "sync", "ios"), sync.command)
        self.assertEqual("xcode:26.3", version.expected_version)
        self.assertIn("CODE_SIGNING_ALLOWED=NO", build.command)
        self.assertIn("generic/platform=iOS Simulator", build.command)

    def test_group_filter_is_explicit_and_version_policies_follow_repository_pins(self):
        checks = preflight.build_plan("local", "windows", groups=("repository", "contracts"))
        self.assertTrue({check.group for check in checks} <= {"repository", "contracts"})
        node = next(check for check in checks if check.name == "Pinned Node.js version")
        dotnet = next(check for check in checks if check.name == "global.json .NET SDK version")
        self.assertEqual("exact:24.21.0", node.expected_version)
        self.assertEqual("latest-patch:10.0.303", dotnet.expected_version)
        self.assertTrue(preflight.version_matches(dotnet.expected_version, "10.0.304"))
        self.assertFalse(preflight.version_matches(dotnet.expected_version, "10.1.303"))
        self.assertFalse(preflight.version_matches(node.expected_version, "v25.9.0"))
        self.assertTrue(preflight.version_matches("xcode:26.3", "Xcode 26.3\nBuild version 17C52"))
        self.assertFalse(preflight.version_matches("xcode:26.3", "Xcode 26.2\nBuild version 17C52"))

    def test_fresh_but_incomplete_report_is_not_accepted(self):
        identity = {"head": "abc", "tree": "def", "worktree_status_sha256": "ghi"}
        report = {**identity, "scope": "all", "selection": "scope", "ci_coverage_complete": True, "complete": True,
                  "missing_platform_coverage": [{"tool": "pio"}]}
        self.assertFalse(preflight.report_is_current_and_complete(report, identity))
        report["missing_platform_coverage"] = []
        self.assertTrue(preflight.report_is_current_and_complete(report, identity))
        report["scope"] = "local"
        report["ci_coverage_complete"] = False
        self.assertTrue(preflight.report_is_current_and_complete(report, identity))
        report["selection"] = "group-filtered"
        self.assertFalse(preflight.report_is_current_and_complete(report, identity))
        self.assertTrue(preflight.identity_changed(identity, {**identity, "worktree_status_sha256": "changed"}))
        self.assertFalse(preflight.identity_changed(identity, dict(identity)))

    def test_unavailable_ci_scanners_are_explicit_missing_checks(self):
        checks = [preflight.Check("actionlint", "repository", "portable", ("actionlint",), tools=("actionlint",)),
                  preflight.Check("gitleaks", "repository", "portable", ("gitleaks",), tools=("gitleaks",))]
        results, missing, failed = preflight.execute_plan(checks, Path("."), lambda *args, **kwargs: None,
                                                           lambda _: None, progress=None)
        self.assertFalse(failed)
        self.assertEqual(["missing", "missing"], [result["status"] for result in results])
        self.assertEqual({"actionlint", "gitleaks"}, {item["tool"] for item in missing})

    def test_command_environment_is_passed_to_child_process(self):
        check = preflight.Check("native", "backend", "portable", ("dotnet", "run"),
                                env=(("PREFLIGHT_TEST_VALUE", "supplied"),))
        captured = {}

        def runner(command, **kwargs):
            captured.update(kwargs)
            return subprocess.CompletedProcess(command, 0)

        preflight.execute_plan([check], Path("/repo"), runner, lambda _: "dotnet", progress=None)
        self.assertEqual("supplied", captured["env"]["PREFLIGHT_TEST_VALUE"])

    def test_git_identity_includes_tracked_tree_and_worktree_status(self):
        outputs = iter((" M tracked.py\n?? new.py", "tracked diff", "new.py\0", "abc123", "tree456"))
        commands = []

        def runner(command, **kwargs):
            commands.append(command)
            return subprocess.CompletedProcess(command, 0, stdout=next(outputs))

        with tempfile.TemporaryDirectory() as directory:
            (Path(directory) / "new.py").write_text("first", encoding="utf-8")
            identity = preflight.tree_fingerprint(Path(directory), runner)
            self.assertEqual("abc123", identity["head"])
            self.assertEqual("tree456", identity["tree"])
            self.assertEqual(5, len(commands))
            self.assertEqual(64, len(identity["worktree_status_sha256"]))

            outputs = iter((" M tracked.py\n?? new.py", "tracked diff", "new.py\0", "abc123", "tree456"))
            (Path(directory) / "new.py").write_text("second", encoding="utf-8")
            changed_identity = preflight.tree_fingerprint(Path(directory), runner)

        self.assertNotEqual(identity["worktree_status_sha256"], changed_identity["worktree_status_sha256"])


if __name__ == "__main__":
    unittest.main()
