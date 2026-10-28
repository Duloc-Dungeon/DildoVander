"""Opt-in real nginx validation of the private character upload boundary."""
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request
import uuid
import keepsake_proxy as proxy


@unittest.skipUnless(os.environ.get('DV_RUN_NGINX_CHARACTER_SMOKE') == '1', 'Opt-in local Docker nginx smoke')
class CharacterAdminProxyTests(unittest.TestCase):
    def test_only_character_portraits_receive_the_larger_body_limit(self):
        name = 'dv-character-proxy-' + uuid.uuid4().hex[:12]
        image = 'nginx:alpine@sha256:a3554c0d0b48177b1413c26e01f695334428020d9df01243fbae3e11cfd17417'
        with tempfile.TemporaryDirectory(prefix='dv-character-proxy-') as temporary:
            root = Path(temporary)
            (root / 'index.html').write_text('<!doctype html><title>Character administration</title>')
            upstream = '''server { listen 5080; location / {
                return 200 "$http_host|$http_x_forwarded_proto|$http_x_dv_csrf|$request_method";
            }}'''
            locations = proxy.render_compatibility('keepsake.example.invalid')
            locations = locations.replace('/var/www/dildovander/current', '/fixture')
            config = 'events {}\nhttp {\n' + proxy.render_http_map() + '\n' + upstream
            config += '\nserver { listen 80;\n' + locations + '\n}}\n'
            (root / 'nginx.conf').write_text(config, encoding='utf-8')
            subprocess.run(['docker', 'run', '--detach', '--rm', '--name', name, '-p', '127.0.0.1::80',
                            '--mount', f'type=bind,source={root},target=/fixture,readonly',
                            image, 'nginx', '-c', '/fixture/nginx.conf', '-g', 'daemon off;'],
                           check=True, capture_output=True, timeout=90)
            try:
                port = subprocess.check_output(['docker', 'port', name, '80/tcp'], text=True, timeout=15).strip().rsplit(':', 1)[1]
                base = 'http://127.0.0.1:' + port

                def request(path, method='GET', data=None):
                    headers = {'Host': 'keepsake.example.invalid', 'Content-Type': 'image/png', 'X-DV-CSRF': 'synthetic'}
                    try:
                        result = urllib.request.urlopen(urllib.request.Request(base + path, data=data, method=method, headers=headers), timeout=10)
                    except urllib.error.HTTPError as error:
                        result = error
                    with result:
                        return result.status, result.headers, result.read()

                for attempt in range(30):
                    try:
                        request('/settings/admin/characters')
                        break
                    except urllib.error.URLError:
                        if attempt == 29:
                            raise
                        time.sleep(.1)
                status, headers, _ = request('/settings/admin/characters')
                self.assertEqual(status, 200)
                self.assertIn('noindex', headers.get('X-Robots-Tag', ''))
                portrait = '/app/v1/admin/characters/' + 'a' * 32 + '/portrait'
                status, headers, body = request(portrait + '?expectedRevision=1', 'PUT', b'x' * (512 * 1024))
                self.assertEqual(status, 200)
                self.assertEqual(body, b'keepsake.example.invalid|https|synthetic|PUT')
                self.assertEqual(headers.get('Cache-Control'), 'no-store')
                self.assertEqual(headers.get('Referrer-Policy'), 'no-referrer')
                self.assertEqual(request(portrait, 'PUT', b'x' * (8 * 1024 * 1024 + 1))[0], 413)
                self.assertEqual(request(portrait.removesuffix('/portrait'), 'PUT', b'x' * (512 * 1024))[0], 413)
                self.assertEqual(request(portrait + '/extra', 'PUT', b'x' * (512 * 1024))[0], 413)
                self.assertEqual(request(portrait, 'POST', b'x')[0], 405)
                self.assertEqual(request(portrait.replace('/app/', '/%61pp/'))[0], 404)
                self.assertEqual(request('/' + portrait)[0], 404)
            finally:
                subprocess.run(['docker', 'stop', '--time', '2', name], check=True, capture_output=True, timeout=15)
