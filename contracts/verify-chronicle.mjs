import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import Ajv from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const contract = JSON.parse(await readFile(new URL('./chronicle-openapi-v1.json', import.meta.url), 'utf8'));
const ajv = new Ajv({ strict: false, allErrors: true });
addFormats(ajv);
ajv.addSchema({ $id: 'chronicle', components: contract.components });
const validate = ajv.compile({ $ref: 'chronicle#/components/schemas/Event' });
const event = {
  id: '00000000-0000-4000-8000-000000000001', sourceKind: 'case', sourceId: 'case-1', groupId: 'group-1',
  occurredAt: 1793275200, title: 'Uzavřený případ', summary: 'Uvolněná rekonstrukce.',
  participants: Array.from({ length: 30 }, (_, index) => ({ accountId: `guest-${index}`, displayName: `Postava ${index}`,
    role: 'participant', visualReferenceVersion: 'unknown' })),
  story: ['Potvrzený příběh.'], decisions: [], awards: [],
  illustrationState: 'pending', revealed: true, revision: 1,
  illustrationSynthetic: false, shareAllowed: true,
};
assert(validate(event), ajv.errorsText(validate.errors));
for (const field of ['result', 'winnerId', 'shareText', 'decisiveChoice']) {
  assert(!validate({ ...event, [field]: null }), `Absent ${field} must be omitted for released mobile clients.`);
  assert(validate({ ...event, [field]: 'recorded-value' }), `Present ${field} remains supported.`);
}
const page = ajv.compile({ $ref: 'chronicle#/components/schemas/Page' });
assert(page({ items: [event], nextCursor: null, badges: [] }), 'The page cursor remains explicitly nullable.');
const leaked = structuredClone(event);
leaked.participants[0].visualSnapshotJson = '{"portraitReferenceBase64":"private"}';
assert(!validate(leaked), 'Public contract must reject private frozen references.');
for (const kind of contract.components.schemas.Kind.enum) {
  assert(validate({ ...event, sourceKind: kind }), `Kind ${kind} is supported.`);
}
const source = await readFile(new URL('../backend/Features/Chronicle/ChronicleEndpoints.cs', import.meta.url), 'utf8');
for (const route of ['/source/{kind}/{sourceId}', '/{id:guid}', '/{id:guid}/reveal', '/{id:guid}/illustration',
  '/{id:guid}/image', '/{id:guid}/share', '/{id:guid}/badges/{badgeId}/image', '/export']) {
  assert(source.includes(`"${route}"`), `Published route ${route} exists.`);
}
assert(source.includes('RequireAuthorization()'), 'Chronicle routes require account authorization.');
console.log('Chronicle contract schemas and route references passed.');
