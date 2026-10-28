"""Exercise promotion selection and settings rollout without GitHub writes."""
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest


ROOT = Path(__file__).resolve().parents[2]
GIT = shutil.which("git")
GIT_BASH = Path(GIT).resolve().parents[1] / "bin/bash.exe" if GIT else Path("missing")
BASH = str(GIT_BASH) if os.name == "nt" and GIT_BASH.is_file() else shutil.which("bash")


@unittest.skipUnless(BASH, "Bash is required to exercise workflow shell commands")
class BranchWorkflowTests(unittest.TestCase):
    def test_promotion_and_recovery_execute_the_intended_project_selection(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        step = workflow.split("      - name: Select all builds for main promotion\n", 1)[1]
        script = textwrap.dedent(step.split("        run: |\n", 1)[1].split("\n  repository:", 1)[0])
        script = script.replace("python3 scripts/ci/select_projects.py", " ".join((
            shlex.quote(Path(sys.executable).as_posix()),
            shlex.quote((ROOT / "scripts/ci/select_projects.py").as_posix()),
        )))
        revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        for event, head, number, project, expected in (
            ("pull_request", "development", "42", "all", "all"),
            ("workflow_dispatch", "development", "42", "all", "all"),
            ("workflow_dispatch", "main", "", "backend", "backend"),
            ("pull_request", "feature", "42", "all", "none"),
        ):
            with self.subTest(event=event, head=head):
                env = {**os.environ, "CI_EVENT_NAME": event, "CI_HEAD_REF": head,
                       "CI_PR_NUMBER": number, "CI_PROJECT": project, "CI_STACK_NUMBER": "",
                       "CI_BASE_SHA": revision, "CI_HEAD_SHA": revision, "GITHUB_OUTPUT": ""}
                result = subprocess.run([BASH, "-c", script], cwd=ROOT, env=env,
                                        capture_output=True, text=True, check=True)
                selected = json.loads(result.stdout)
                enabled = {name for name, value in selected.items() if value}
                self.assertEqual(enabled, set(selected) if expected == "all" else
                                 set() if expected == "none" else {expected})

    def run_settings(self, mode="normal", apply=True):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "scripts/ci").mkdir(parents=True)
            (root / ".github").mkdir()
            (root / "bin").mkdir()
            (root / "home").mkdir()
            shutil.copyfile(ROOT / "scripts/ci/apply-repository-settings.sh",
                            root / "scripts/ci/apply-repository-settings.sh")
            for name in ("repository-settings.json", "main-ruleset.json", "development-ruleset.json"):
                shutil.copyfile(ROOT / ".github" / name, root / ".github" / name)
            fake = root / "bin/gh"
            fake.write_text(textwrap.dedent("""\
                #!/usr/bin/env bash
                printf '%s\\n' "$*" >> "$GITHUB_TEST_LOG"
                if [[ "$*" == *'--paginate'* ]]; then
                  if [[ "$GITHUB_TEST_MODE" == readfail ]]; then exit 9; fi
                  if [[ "$GITHUB_TEST_MODE" == duplicate ]]; then printf '1\\n2\\n'; exit 0; fi
                  if [[ "$*" == *'main quality gate'* ]]; then echo 123; fi
                fi
                """), encoding="utf-8", newline="\n")
            fake.chmod(0o755)
            log = root / "calls.log"
            env = {**os.environ, "HOME": (root / "home").as_posix(),
                   "PATH": str(root / "bin") + os.pathsep + os.environ["PATH"],
                   "GITHUB_TEST_LOG": log.as_posix(), "GITHUB_TEST_MODE": mode}
            result = subprocess.run([BASH, "scripts/ci/apply-repository-settings.sh",
                                     *(["--apply"] if apply else [])],
                                    cwd=root, env=env, capture_output=True, text=True)
            return result, log.read_text() if log.exists() else ""

    def test_settings_preview_never_calls_github(self):
        result, calls = self.run_settings(apply=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("development quality gate", result.stdout)
        self.assertEqual(calls, "")

    def test_settings_apply_updates_main_and_creates_development_after_preflight(self):
        result, calls = self.run_settings()
        self.assertEqual(result.returncode, 0, result.stderr)
        operations = calls.splitlines()
        self.assertEqual(len(operations), 5)
        self.assertTrue(all("--paginate" in line for line in operations[:2]))
        self.assertIn("--method PATCH", operations[2])
        self.assertIn("--method PUT repos/Duloc-Dungeon/DildoVander/rulesets/123", operations[3])
        self.assertIn(".github/main-ruleset.json", operations[3])
        self.assertIn("--method POST repos/Duloc-Dungeon/DildoVander/rulesets", operations[4])
        self.assertIn(".github/development-ruleset.json", operations[4])

    def test_settings_fail_without_writes_when_preflight_cannot_identify_rules(self):
        for mode in ("duplicate", "readfail"):
            with self.subTest(mode=mode):
                result, calls = self.run_settings(mode=mode)
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn("--method", calls)


if __name__ == "__main__":
    unittest.main()
