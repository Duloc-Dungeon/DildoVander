"""Keep CI bootstrap scripts and their data-only inputs in the checkout."""
from pathlib import Path
import re
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT_REFERENCE = re.compile(r"\bscripts/ci/[A-Za-z0-9_-]+\.(?:py|cjs)\b")
BOOTSTRAP_INPUTS = (
    "scripts/generate_design_tokens.py",
    "scripts/check_design_system.py",
    "mechanika/final/source_inputs.py",
    "design-system/tokens.json",
    "design-system/theme-runtime.js",
    "design-system/theme-controls.js",
    "design-system/package.json",
    "devtools/workspace/Sync-Workspace.ps1",
    "devtools/workspace/templates/extensions.template.json",
    "devtools/workspace/templates/launch.workspace.template.json",
    "devtools/workspace/templates/settings.template.json",
    "devtools/workspace/templates/tasks.template.json",
    "devtools/workspace/templates/workspace.template.json",
)


def missing_inputs(root):
    required = set(BOOTSTRAP_INPUTS)
    for workflow in (root / ".github/workflows").glob("*.yml"):
        required.update(SCRIPT_REFERENCE.findall(workflow.read_text(encoding="utf-8")))
    return sorted(path for path in required if not (root / path).is_file())


class WorkflowInputsTests(unittest.TestCase):
    def test_existing_workflows_keep_their_bootstrap_inputs(self):
        self.assertEqual([], missing_inputs(ROOT))

    def test_deleted_bootstrap_or_workflow_script_is_reported(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workflows = root / ".github/workflows"
            workflows.mkdir(parents=True)
            (workflows / "ci.yml").write_text(
                "run: python3 scripts/ci/select_projects.py\n"
                "script: require('./scripts/ci/pr-freshness.cjs')\n",
                encoding="utf-8",
            )
            required = (*BOOTSTRAP_INPUTS, "scripts/ci/select_projects.py", "scripts/ci/pr-freshness.cjs")
            for relative in required:
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.touch()
            self.assertEqual([], missing_inputs(root))
            for relative in required:
                with self.subTest(path=relative):
                    path = root / relative
                    path.unlink()
                    self.assertEqual([relative], missing_inputs(root))
                    path.touch()


if __name__ == "__main__":
    unittest.main()
