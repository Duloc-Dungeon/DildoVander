import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import android


class ReleaseVerificationTests(unittest.TestCase):
    def test_release_origin_rejects_local_and_insecure_endpoints(self):
        for origin in ('http://example.com', 'https://localhost', 'https://10.0.0.1',
                       'https://host.ts.net', 'https://server.local', 'https://user:password@example.com',
                       'https://example.com/app', 'https://[::1]'):
            with self.subTest(origin=origin), self.assertRaises(ValueError):
                android.validate_origin(origin)
        android.validate_origin('https://dildovander.de')

    def test_apk_signature_package_version_and_digest_are_checked(self):
        certificate = 'a' * 64
        signatures = f'Signer #1 certificate SHA-256 digest: {certificate}\nSigner #1 certificate DN: CN=Wand Night Stand\n'
        badging = "package: name='de.dildovander.companion' versionCode='23' versionName='0.1.0'\n"
        with tempfile.TemporaryDirectory() as directory:
            apk = Path(directory) / 'release.apk'
            apk.write_bytes(b'signed APK fixture')
            with patch.object(android, 'sdk_tool', side_effect=lambda name: name), patch.object(android, 'run') as run:
                run.side_effect = [subprocess.CompletedProcess([], 0, signatures), subprocess.CompletedProcess([], 0, badging)]
                result = android.apk_metadata(apk, 23, certificate)
                self.assertEqual(result['application_id'], android.APPLICATION_ID)
                self.assertEqual(result['version_code'], 23)
                self.assertEqual(len(result['apk_sha256']), 64)
                for signature, package, version in (
                    (signatures, badging, 24),
                    (signatures, badging.replace('de.dildovander.companion', 'wrong.package'), 23),
                    (signatures.replace('CN=Wand Night Stand', 'CN=Android Debug'), badging, 23),
                    (signatures.replace(certificate, 'b' * 64), badging, 23),
                    (signatures, badging + 'application-debuggable\n', 23),
                ):
                    run.side_effect = [subprocess.CompletedProcess([], 0, signature), subprocess.CompletedProcess([], 0, package)]
                    with self.assertRaises(ValueError):
                        android.apk_metadata(apk, version, certificate)

    def test_apksigner_failure_cannot_be_treated_as_verified(self):
        with tempfile.NamedTemporaryFile(suffix='.apk') as apk:
            with patch.object(android, 'sdk_tool', return_value='apksigner'), patch.object(android, 'run', side_effect=subprocess.CalledProcessError(1, 'apksigner')):
                with self.assertRaises(subprocess.CalledProcessError):
                    android.apk_metadata(apk.name)


if __name__ == '__main__':
    unittest.main()
