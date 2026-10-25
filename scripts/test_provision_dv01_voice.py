import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError, URLError

from provision_dv01_voice import provision, PROMPT, SCRIPT_HINT


class VoiceProvisioningTests(unittest.TestCase):
    def test_completed_concurrent_creation_is_reused_under_lock(self):
        import os
        with tempfile.TemporaryDirectory() as folder:
            state = Path(folder) / "voice.json"
            original_open = os.open
            def racing_open(path, flags, mode=0o777):
                descriptor = original_open(path, flags, mode)
                if str(path).endswith(".pending"):
                    state.write_text('{"customVoiceId":"voice_concurrent"}', encoding="utf-8")
                return descriptor
            def forbidden(*args, **kwargs):
                self.fail("A completed concurrent creation must not issue another request.")
            with patch("provision_dv01_voice.os.open", racing_open):
                saved, created = provision(state, "fixture-key", send=forbidden)
            self.assertFalse(created)
            self.assertEqual(saved["customVoiceId"], "voice_concurrent")
            self.assertFalse(state.with_suffix(".json.pending").exists())

    def test_reuses_saved_voice_without_provider_call(self):
        with tempfile.TemporaryDirectory() as folder:
            state = Path(folder) / "voice.json"
            calls = []
            def send(request, **kwargs):
                calls.append(json.loads(request.data))
                return io.StringIO('{"id":"voice_fixture","created_at":123}')
            first, created = provision(state, "fixture-key", send=send)
            second, repeated = provision(state, "fixture-key", send=send)
            self.assertTrue(created)
            self.assertFalse(repeated)
            self.assertEqual(first, second)
            self.assertEqual(len(calls), 1)
            self.assertEqual(calls[0]["type"], "prompt")
            self.assertEqual(calls[0]["prompt"], PROMPT)
            self.assertEqual(calls[0]["script_hint"], SCRIPT_HINT)
            self.assertNotIn("fixture-key", state.read_text(encoding="utf-8"))

    def test_unknown_outcome_blocks_duplicate_request(self):
        with tempfile.TemporaryDirectory() as folder:
            state = Path(folder) / "voice.json"
            def fail(*args, **kwargs):
                raise URLError("fixture transport failure")
            with self.assertRaisesRegex(ValueError, "outcome unknown"):
                provision(state, "fixture-key", send=fail)
            with self.assertRaises(FileExistsError):
                provision(state, "fixture-key", send=fail)

    def test_rejected_access_does_not_save_voice_or_secret(self):
        with tempfile.TemporaryDirectory() as folder:
            state = Path(folder) / "voice.json"
            def reject(*args, **kwargs):
                raise HTTPError("https://api.openai.com", 403, "secret provider body", {}, None)
            with self.assertRaisesRegex(ValueError, "HTTP 403"):
                provision(state, "fixture-key", send=reject)
            self.assertFalse(state.exists())
            self.assertFalse(state.with_suffix(".json.pending").exists())

    def test_replacement_preserves_prior_selection(self):
        with tempfile.TemporaryDirectory() as folder:
            state = Path(folder) / "voice.json"
            def send(*args, **kwargs):
                return io.StringIO('{"id":"voice_new"}')
            state.write_text('{"customVoiceId":"voice_old"}', encoding="utf-8")
            saved, created = provision(state, "fixture-key", replace=True, send=send)
            self.assertTrue(created)
            self.assertEqual(saved["customVoiceId"], "voice_new")
            self.assertEqual(json.loads(state.with_name("voice.json.previous").read_text())["customVoiceId"], "voice_old")


if __name__ == "__main__":
    unittest.main()
