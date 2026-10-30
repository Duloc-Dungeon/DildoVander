"""Execute the actual development gate with successful and incomplete jobs."""
import json
import os
from pathlib import Path
import subprocess
import sys
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]

class DevelopmentGateTests(unittest.TestCase):
    def gate(self, selected, results):
        workflow = (ROOT / '.github/workflows/development-ci.yml').read_text()
        block = workflow.split("          python3 - <<'PY'\n")[-1].split('          PY')[0]
        return subprocess.run([sys.executable, '-c', textwrap.dedent(block)],
            env={**os.environ, 'SELECTED': json.dumps(selected), 'RESULTS': json.dumps(results)},
            capture_output=True, text=True).returncode

    def fixture(self):
        selected = {name: 'false' for name in ('backend', 'firmware', 'web', 'ios', 'android', 'ai-eval', 'site')}
        selected['backend'] = 'true'
        results = {name: {'result': 'success' if value == 'true' else 'skipped'} for name, value in selected.items()}
        results.update({name: {'result': 'success'} for name in ('candidate', 'secret-scan', 'repository', 'changes')})
        return selected, results

    def test_selected_products_must_pass(self):
        selected, results = self.fixture()
        self.assertEqual(self.gate(selected, results), 0)
        for status in ('failure', 'cancelled', 'skipped'):
            results['backend']['result'] = status
            self.assertNotEqual(self.gate(selected, results), 0)

    def test_failed_selection_and_unexpected_jobs_cannot_pass(self):
        selected, results = self.fixture()
        results['changes']['result'] = 'failure'
        self.assertNotEqual(self.gate({}, results), 0)
        results['changes']['result'] = 'success'
        results['site']['result'] = 'failure'
        self.assertNotEqual(self.gate(selected, results), 0)

if __name__ == '__main__':
    unittest.main()
