import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import Ajv from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
const document = JSON.parse(await readFile(new URL('./game-openapi-v1.json', import.meta.url), 'utf8'));
const ingress = await readFile(new URL('../web/deploy/keepsake_proxy.py', import.meta.url), 'utf8');
assert.ok(ingress.includes('location ^~ /app/v1/ {'), 'account API proxy is required');
const ajv = new Ajv({ strict: true, allErrors: true }); addFormats(ajv);
for (const [name, schema] of Object.entries(document.components.schemas)) {
  ajv.addSchema(JSON.parse(JSON.stringify(schema).replaceAll('#/components/schemas/', 'urn:game:')), `urn:game:${name}`);
}
for (const name of Object.keys(document.components.schemas)) assert.ok(ajv.getSchema(`urn:game:${name}`));
for (const [path, methods] of Object.entries(document.paths)) for (const [method, operation] of Object.entries(methods)) {
  assert.deepEqual(operation.security, [{ AccountSession: [] }]);
  assert.match(path, /^\/app\/v1\/(?:game(?:\/|$)|admin\/game\/)/);
  assert.ok(operation['x-source'].startsWith('Features/Game/'));
  if (method === 'post') assert.ok(operation.parameters.some(parameter => parameter.name === 'X-DV-CSRF' && parameter.required));
}
const uuid = '11111111-1111-4111-8111-111111111111';
const command = ajv.getSchema('urn:game:GameCommand');
assert.ok(command({ operationId: uuid, expectedRevision: 1, choice: 'trick', sparkBid: 1 }));
assert.equal(command({ operationId: uuid, expectedRevision: 1, choice: 'trick', sparkBid: 2 }), false);
assert.equal(command({ operationId: uuid, expectedRevision: -1 }), false);
assert.equal(command({ operationId: uuid, expectedRevision: 1, choice: 'fireball' }), false);
const team = ajv.getSchema('urn:game:GameTeamCreate');
assert.ok(team({ operationId: uuid, partnerId: 'one', opponentIds: ['two', 'three'] }));
assert.equal(team({ operationId: uuid, partnerId: 'one', opponentIds: ['two', 'two'] }), false);
const boss = ajv.getSchema('urn:game:GameBossCommand');
assert.ok(boss({ operationId: uuid, expectedRevision: 1, choice: 'shield', sparkBid: 1 }));
assert.equal(boss({ operationId: 'not-a-guid', expectedRevision: 1 }), false);
console.log('Mobile game OpenAPI: schemas, cookie/CSRF boundaries, account ingress and command bounds passed.');
