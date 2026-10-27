// Test-fixture diagnostics only. Never read rows, credentials or provider settings.
import { spawnSync } from 'node:child_process';

const inspect = `
import json, pathlib, sqlite3, sys
try:
    with sqlite3.connect(pathlib.Path(sys.argv[1]).resolve().as_uri() + '?mode=ro', uri=True) as connection:
        print(json.dumps({
            'available': True,
            'sqliteVersion': sqlite3.sqlite_version,
            'schemaVersion': connection.execute('PRAGMA schema_version').fetchone()[0],
            'userVersion': connection.execute('PRAGMA user_version').fetchone()[0],
            'tables': [row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")],
        }))
except (OSError, sqlite3.Error):
    print(json.dumps({'available': False, 'reason': 'sqlite_unavailable'}))
`;

export function readSqliteStartupDiagnostic(databasePath) {
  const result = spawnSync(process.platform === 'win32' ? 'python' : 'python3',
    ['-c', inspect, databasePath], { encoding: 'utf8', timeout: 5000, maxBuffer: 65536, windowsHide: true });
  if (result.error || result.status !== 0) return { available: false, reason: 'inspection_unavailable' };
  try { return JSON.parse(result.stdout); }
  catch { return { available: false, reason: 'inspection_unavailable' }; }
}
