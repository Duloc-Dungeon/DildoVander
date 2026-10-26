import { createOpeningFixtureServer } from './fixtures/opening.mjs';
const { origin } = await createOpeningFixtureServer(Number(process.env.PORT ?? 5178));
console.log(`Synthetic opening preview: ${origin}/__opening`);
