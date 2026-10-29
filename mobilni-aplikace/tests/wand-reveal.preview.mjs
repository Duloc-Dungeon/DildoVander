// Saved paid image, fictional session, loopback only. This preview never calls a provider.
import { createWorkshopFixtureServer } from './fixtures/wand-workshop.mjs';

const waitingRoom = process.argv.includes('--waiting-room');
const fixture = await createWorkshopFixtureServer(0, { reference: true });
await fetch(`${fixture.origin}/__workshop/${waitingRoom ? 'prelaunch' : 'ready'}`);
const base = `${fixture.origin}/app/v1/customer/wand/workshop`;
await fetch(`${base}/sessions`, { method: 'POST', body: JSON.stringify({ upgrade: false }) });
const { session } = await (await fetch(base)).json();
await fetch(`${base}/sessions/${session.id}/finish`, { method: 'POST', body: '{}' });
await fetch(base);
await fetch(`${base}/sessions/${session.id}/artwork/retry`, { method: 'POST', body: '{}' });
console.log(`Saved-reference preview: ${fixture.origin}${waitingRoom ? '/launch' : '/play/wand'}`);
console.log(waitingRoom ? 'Use the mirror replay control to repeat the waiting-room reveal.'
  : 'Reload to replay the full reveal. Pointer movement only bends an active reveal.');
