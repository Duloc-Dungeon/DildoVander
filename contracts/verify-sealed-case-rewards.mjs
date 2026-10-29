import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import Ajv from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const document = JSON.parse(await readFile(new URL('./sealed-case-rewards-openapi-v1.json', import.meta.url), 'utf8'));
const route = document.paths['/app/v1/sealed-cases/rewards']?.get;
assert.ok(route, 'the participant reward read route is documented');
assert.deepEqual(route.security, [{ AccountSession: [] }]);
assert.equal(route['x-source'], 'Features/SealedCase/SealedCaseRewards.cs');

const ajv = new Ajv({ strict: true, allErrors: true });
addFormats(ajv);
for (const [name, schema] of Object.entries(document.components.schemas)) {
  const normalized = JSON.parse(JSON.stringify(schema)
    .replaceAll('#/components/schemas/', 'urn:sealed-case:'));
  ajv.addSchema(normalized, `urn:sealed-case:${name}`);
}
const validate = ajv.getSchema('urn:sealed-case:SealedCaseRewardCollection');
assert.ok(validate, 'collection schema compiles');
const fixture = {
  schemaVersion: 1,
  items: [{
    caseId: 'sc_11111111111111111111111111111111',
    releasedAt: '2026-10-28T10:00:00Z',
    team: 'Guardian',
    confirmedPlans: 5,
    successfulPlans: 2
  }]
};
assert.ok(validate(fixture));
assert.equal(validate({ ...fixture, items: [{ ...fixture.items[0], privateNotes: 'secret' }] }), false);
assert.equal(validate({ ...fixture, items: [{ ...fixture.items[0], team: 'unknown' }] }), false);
const scenario = { id: 'HP_SPOLECENSTVI_KRBU_V1', title: 'Společenství vlastníků krbu', teamLabel: 'Sousedé' };
assert.ok(validate({ ...fixture, items: [{ ...fixture.items[0], scenario }] }), 'opt-in scenario metadata validates');
assert.equal(validate({ ...fixture, items: [{ ...fixture.items[0], scenario: { ...scenario, covertActions: ['secret'] } }] }), false);
assert.equal(validate({ ...fixture, items: [{ ...fixture.items[0], scenario: { ...scenario, teamLabel: '' } }] }), false);
assert.equal(route.parameters.find(parameter => parameter.name === 'scenarioMetadata')?.schema.default, false);
console.log('Sealed-case reward contract: session scope, legacy response, opt-in scenario metadata and closed projections passed.');
