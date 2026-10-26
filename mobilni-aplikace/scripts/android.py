#!/usr/bin/env python3
"""Build the shared Capacitor app and verify the exact APK before publication."""
import argparse
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[1]
APPLICATION_ID = 'de.dildovander.companion'


def run(arguments, **kwargs):
    return subprocess.run([str(value) for value in arguments], check=True, cwd=ROOT, **kwargs)


def sdk_path():
    path = os.environ.get('ANDROID_HOME') or os.environ.get('ANDROID_SDK_ROOT')
    if not path and os.name == 'nt':
        path = str(Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk')
    if not path or not Path(path).is_dir():
        raise ValueError('Set ANDROID_HOME to an Android SDK with platform 36 and build-tools 36.0.0')
    return Path(path)


def sdk_tool(name):
    suffix = '.bat' if name == 'apksigner' else '.exe'
    path = sdk_path() / 'build-tools/36.0.0' / (name + (suffix if os.name == 'nt' else ''))
    if not path.is_file():
        raise ValueError(f'Missing {path}; install Android build-tools 36.0.0')
    return path


def validate_origin(origin):
    parsed = urlparse(origin)
    host = parsed.hostname or ''
    if (parsed.scheme != 'https' or not host or parsed.username or parsed.password
            or parsed.path not in ('', '/') or parsed.query or parsed.fragment
            or host == 'localhost' or host.endswith(('.localhost', '.local', '.internal', '.ts.net'))
            or '.' not in host):
        raise ValueError('Release backend must be a public HTTPS origin')
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        return
    if not address.is_global:
        raise ValueError('Private IP addresses cannot be used for friend releases')


def apk_metadata(apk, expected_code=None, expected_certificate=None):
    apk = Path(apk).resolve()
    if not apk.is_file():
        raise ValueError(f'APK missing: {apk}')
    signature = run([sdk_tool('apksigner'), 'verify', '--verbose', '--print-certs', apk],
                    capture_output=True, text=True).stdout
    certificates = re.findall(r'^Signer #\d+ certificate SHA-256 digest: ([a-fA-F0-9]{64})$', signature, re.M)
    if len(certificates) != 1:
        raise ValueError('Exactly one verified APK signing identity is required')
    certificate = certificates[0].lower()
    if expected_certificate and certificate != expected_certificate.lower().replace(':', ''):
        raise ValueError('APK signing certificate does not match the expected release key')
    if re.search(r'^Signer #\d+ certificate DN:.*CN=Android Debug', signature, re.M):
        raise ValueError('A debug signing certificate cannot be distributed')
    badging = run([sdk_tool('aapt'), 'dump', 'badging', apk], capture_output=True, text=True).stdout
    package = re.search(r"^package: name='([^']+)' versionCode='(\d+)' versionName='([^']+)'", badging, re.M)
    if not package or package[1] != APPLICATION_ID:
        raise ValueError('APK package ID mismatch')
    if expected_code is not None and int(package[2]) != expected_code:
        raise ValueError('APK versionCode does not match the requested build')
    if "application-debuggable" in badging:
        raise ValueError('A debuggable APK cannot be distributed')
    return {'application_id': package[1], 'version_code': int(package[2]), 'version_name': package[3],
            'signing_certificate_sha256': certificate,
            'apk_sha256': hashlib.sha256(apk.read_bytes()).hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('debug', 'run', 'release', 'verify'))
    parser.add_argument('--version-code', type=int, default=None)
    parser.add_argument('--version-name', default=json.loads((ROOT / 'package.json').read_text())['version'])
    parser.add_argument('--skip-web', action='store_true', help='Use an already verified CI web artifact')
    parser.add_argument('--apk', type=Path)
    args = parser.parse_args()
    if args.command == 'verify':
        if not args.apk:
            parser.error('verify requires --apk')
        print(json.dumps(apk_metadata(args.apk, args.version_code, os.environ.get('ANDROID_SIGNING_CERTIFICATE_SHA256')), indent=2))
        return
    release = args.command == 'release'
    java = Path(os.environ['JAVA_HOME']) / 'bin/java' if os.environ.get('JAVA_HOME') else shutil.which('java')
    if not java:
        raise ValueError('Install JDK 21 and set JAVA_HOME')
    version = run([java, '-version'], capture_output=True, text=True)
    if not re.search(r'version "21\.', version.stdout + version.stderr):
        raise ValueError('This Capacitor Android build requires JDK 21; set JAVA_HOME to that JDK')
    code = args.version_code or int(os.environ.get('ANDROID_VERSION_CODE', '0'))
    if release:
        if not 1 <= code <= 2100000000:
            raise ValueError('Release requires --version-code (1..2100000000), greater than all previously distributed versions')
        if not re.fullmatch(r'[0-9]+(?:\.[0-9]+){1,2}(?:[-+][A-Za-z0-9.]+)?', args.version_name):
            raise ValueError('Version name must be a numeric release version')
        for key in ('ANDROID_KEYSTORE_PATH', 'ANDROID_KEYSTORE_PASSWORD'):
            if not os.environ.get(key):
                raise ValueError(f'Release requires {key}; on Windows use scripts/android.ps1 after init-android-signing.ps1')
        if not Path(os.environ['ANDROID_KEYSTORE_PATH']).is_file():
            raise ValueError('Configured release keystore does not exist')
        validate_origin(os.environ.get('KUFR_ACCOUNT_ORIGIN') or 'https://dildovander.de')
    os.environ['ANDROID_VERSION_CODE'] = str(code or 1)
    os.environ['ANDROID_VERSION_NAME'] = args.version_name
    os.environ['ANDROID_HOME'] = str(sdk_path())
    # local.properties paths use Gradle's forward slash notation on Windows.
    (ROOT / 'android/local.properties').write_text('sdk.dir=' + sdk_path().as_posix().replace(':', '\\:') + '\n')
    npm = shutil.which('npm.cmd' if os.name == 'nt' else 'npm')
    if not npm:
        raise ValueError('Node/npm is required')
    if not args.skip_web:
        run([npm, 'run', 'build'])
    run([shutil.which('node'), 'scripts/generate-android-assets.mjs'])
    run([npm, 'exec', '--', 'cap', 'sync', 'android'])
    gradle = ROOT / 'android' / ('gradlew.bat' if os.name == 'nt' else 'gradlew')
    subprocess.run([str(gradle), '--no-daemon', '-p', str(ROOT / 'android'),
                    'assembleRelease' if release else 'assembleDebug',
                    'testReleaseUnitTest' if release else 'testDebugUnitTest',
                    'lintRelease' if release else 'lintDebug'],
                   check=True, cwd=ROOT)
    if release:
        apk = ROOT / 'android/app/build/outputs/apk/release/app-release.apk'
        metadata = apk_metadata(apk, code, os.environ.get('ANDROID_SIGNING_CERTIFICATE_SHA256'))
        output = ROOT / 'dist/android'
        output.mkdir(parents=True, exist_ok=True)
        shutil.copy2(apk, output / 'app-release.apk')
        (output / 'release-metadata.json').write_text(json.dumps(metadata, indent=2) + '\n')
        print(json.dumps({'apk': str(output / 'app-release.apk'), **metadata}, indent=2))
    elif args.command == 'run':
        adb = sdk_path() / 'platform-tools' / ('adb.exe' if os.name == 'nt' else 'adb')
        run([adb, 'install', '-r', ROOT / 'android/app/build/outputs/apk/debug/app-debug.apk'])
        run([adb, 'shell', 'am', 'start', '-n', APPLICATION_ID + '/.MainActivity'])


if __name__ == '__main__':
    try:
        # Short, canonical Windows paths avoid AF_UNIX socket failures in JVM
        # selectors when TEMP is an 8.3 alias or exceeds the socket path limit.
        if os.name == 'nt':
            with tempfile.TemporaryDirectory(prefix='.wns-socket-', dir=Path.home()) as socket_dir:
                os.environ['JAVA_TOOL_OPTIONS'] = (os.environ.get('JAVA_TOOL_OPTIONS', '')
                    + ' "-Djdk.net.unixdomain.tmpdir=' + Path(socket_dir).as_posix() + '"').strip()
                main()
        else:
            main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        # Never echo process environments (signing secrets live there).
        print(f'Android: {error}', file=sys.stderr)
        sys.exit(1)
