import { createSealedCaseFixtureServer, sealedCaseId } from './fixtures/sealed-case.mjs';
const preview = await createSealedCaseFixtureServer();
console.log(`${preview.origin}/play/pod-peceti/${sealedCaseId}`);
for (const signal of ['SIGINT', 'SIGTERM']) process.once(signal, async () => { await preview.close(); process.exit(0); });
