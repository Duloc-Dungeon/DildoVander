import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { readSqliteStartupDiagnostic } from './sqlite-startup-diagnostic.mjs';

const python = process.platform === 'win32' ? 'python' : 'python3';

test('startup diagnostics read schema metadata without changing the database or exposing rows', async () => {
  const root = await mkdtemp(join(tmpdir(), 'sqlite startup diagnostic '));
  try {
    const database = join(root, 'fixture.sqlite');
    const seeded = spawnSync(python, ['-c',
      'import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute("CREATE TABLE show_duel_commentary (result TEXT)"); c.execute("INSERT INTO show_duel_commentary VALUES (?)", ("synthetic-private-content",)); c.execute("PRAGMA user_version=8"); c.commit(); c.close()',
      database], { encoding: 'utf8' });
    assert.equal(seeded.status, 0, seeded.stderr);
    const before = createHash('sha256').update(await readFile(database)).digest('hex');
    const diagnostic = readSqliteStartupDiagnostic(database);
    assert.equal(diagnostic.available, true);
    assert.deepEqual(diagnostic.tables, ['show_duel_commentary']);
    assert.equal(diagnostic.userVersion, 8);
    assert.equal(typeof diagnostic.sqliteVersion, 'string');
    assert.equal(typeof diagnostic.schemaVersion, 'number');
    assert.ok(!JSON.stringify(diagnostic).includes('synthetic-private-content'));
    assert.equal(createHash('sha256').update(await readFile(database)).digest('hex'), before);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('diagnostics of a missing database never create one', async () => {
  const root = await mkdtemp(join(tmpdir(), 'sqlite diagnostic missing '));
  try {
    const database = join(root, 'missing.sqlite');
    assert.equal(readSqliteStartupDiagnostic(database).available, false);
    await assert.rejects(stat(database), { code: 'ENOENT' });
  } finally { await rm(root, { recursive: true, force: true }); }
});
