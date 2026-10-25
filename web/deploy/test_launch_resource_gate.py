"""Opt-in real nginx smoke of the checked-in resource gate; synthetic files only."""
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request
import uuid


@unittest.skipUnless(os.environ.get('DV_RUN_NGINX_LAUNCH_SMOKE') == '1', 'Opt-in local Docker nginx smoke')
class LaunchResourceGateTests(unittest.TestCase):
    def test_real_nginx_rejects_anonymous_assets_and_forwards_verified_context(self):
        name = 'dv-launch-smoke-' + uuid.uuid4().hex[:12]
        with tempfile.TemporaryDirectory(prefix='dv-launch-nginx-') as temporary:
            root = Path(temporary)
            site = root / 'site'
            site.mkdir()
            (site / 'index.html').write_text('<!doctype html><title>Invitation</title>')
            model = 'a' * 64
            paths = ['/models/active.json', f'/models/bundles/{model}/model.json', '/downloads/index.json',
                     f'/downloads/releases/{model}/synthetic.zip']
            for resource in paths:
                path = root / 'data' / resource.lstrip('/')
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b'{"synthetic":true}')
            locations = Path(__file__).with_name('site-locations.conf').read_text()
            locations = locations.replace('/var/www/dildovander/current', '/fixture/site')
            locations = locations.replace('/var/www/dildovander', '/fixture/site')
            locations = locations.replace('/var/lib/dildovander', '/fixture/data')
            # The fake upstream represents only the auth decision; real passkey/role policy has HTTPS tests.
            upstream = '''server { listen 5080; location = /app/v1/launch/resource-access {
                if ($http_cookie = "dv-test-staff=verified") { return 204; }
                if ($http_authorization = "Bearer synthetic-resource-readback") { return 204; }
                return 403;
            }}'''
            (root / 'nginx.conf').write_text('events {}\nhttp { ' + upstream + '\nserver { listen 80;\n' + locations + '\n}}\n')
            image = 'nginx:alpine@sha256:a3554c0d0b48177b1413c26e01f695334428020d9df01243fbae3e11cfd17417'
            subprocess.run(['docker', 'run', '--detach', '--rm', '--name', name, '-p', '127.0.0.1::80',
                            '--mount', f'type=bind,source={root},target=/fixture,readonly',
                            image, 'nginx', '-c', '/fixture/nginx.conf', '-g', 'daemon off;'],
                           check=True, capture_output=True, timeout=45)
            try:
                port = subprocess.check_output(['docker', 'port', name, '80/tcp'], text=True, timeout=15).strip().rsplit(':', 1)[1]
                base = 'http://127.0.0.1:' + port

                def fetch(path, headers=None):
                    try:
                        response = urllib.request.urlopen(urllib.request.Request(base + path, headers=headers or {}), timeout=5)
                    except urllib.error.HTTPError as error:
                        response = error
                    with response:
                        return response.status, response.headers, response.read()

                for attempt in range(30):
                    try:
                        fetch('/launch')
                        break
                    except urllib.error.URLError:
                        if attempt == 29:
                            raise
                        time.sleep(.1)
                status, headers, _ = fetch('/launch')
                self.assertEqual(status, 200)
                self.assertIn('noindex', headers.get('X-Robots-Tag', ''))
                self.assertEqual(fetch('/_dv_launch_access')[0], 404)
                for resource in paths:
                    with self.subTest(resource=resource):
                        self.assertEqual(fetch(resource)[0], 403)
                        self.assertEqual(fetch(resource, {'Cookie': 'dv-test-staff=forged'})[0], 403)
                        status, headers, body = fetch(resource, {'Cookie': 'dv-test-staff=verified'})
                        self.assertEqual(status, 200)
                        self.assertIn('no-store', headers.get('Cache-Control', ''))
                        self.assertEqual(body, b'{"synthetic":true}')
                        self.assertEqual(fetch(resource)[0], 403, 'Authorized response must not populate a public cache')
                        self.assertEqual(fetch(resource, {'Authorization': 'Bearer synthetic-resource-readback'})[0], 200)
                self.assertEqual(fetch(f'/models/bundles/{model}/absent.json', {'Cookie': 'dv-test-staff=verified'})[0], 404)
            finally:
                subprocess.run(['docker', 'stop', '--time', '2', name], check=True, capture_output=True, timeout=15)
