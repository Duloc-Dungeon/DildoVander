import json
import os
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path


class LocalPackageTests(unittest.TestCase):
    def test_local_manifest_uses_actual_git_identity_and_ignores_actions_environment(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            ci = root / "scripts/ci"
            ci.mkdir(parents=True)
            shutil.copy2(Path(__file__).with_name("package.py"), ci / "package.py")
            helper = ci / "local_package.py"
            shutil.copy2(Path(__file__).with_name("local_package.py"), helper)
            payload = root / "payload"
            payload.mkdir()
            (payload / "app.txt").write_text("local artifact", encoding="utf-8")

            subprocess.run(["git", "init", "--quiet"], cwd=root, check=True)
            subprocess.run(["git", "add", "."], cwd=root, check=True)
            subprocess.run(["git", "-c", "user.name=Preflight Test", "-c",
                            "user.email=preflight@example.invalid", "commit", "--quiet", "-m", "fixture"],
                           cwd=root, check=True)
            expected_head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
            expected_tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], cwd=root,
                                                    text=True).strip()

            env = os.environ.copy()
            env.update({
                "GITHUB_ACTIONS": "true",
                "GITHUB_EVENT_NAME": "pull_request",
                "GITHUB_REPOSITORY": "wrong/repository",
                "GITHUB_RUN_ID": "12345",
                "GITHUB_RUN_ATTEMPT": "8",
                "GITHUB_EVENT_PATH": str(root / "forged-event.json"),
            })
            destination = root / "package"
            subprocess.run([sys.executable, str(helper), str(payload), str(destination)],
                           cwd=root, env=env, check=True)

            manifest = json.loads((destination / "build-manifest.json").read_text(encoding="utf-8"))
            self.assertEqual("local_preflight", manifest["event"])
            self.assertEqual("local/preflight", manifest["repository"])
            self.assertEqual(0, manifest["run_id"])
            self.assertEqual(0, manifest["run_attempt"])
            self.assertEqual(expected_head, manifest["source_sha"])
            self.assertEqual(expected_tree, manifest["source_tree"])
            self.assertEqual(expected_head, manifest["head_sha"])
            self.assertIsNone(manifest["base_sha"])
            self.assertIsNone(manifest["pr_number"])
            with tarfile.open(destination / "payload.tar.gz", "r:gz") as archive:
                self.assertEqual(b"local artifact", archive.extractfile("app.txt").read())


if __name__ == "__main__":
    unittest.main()
