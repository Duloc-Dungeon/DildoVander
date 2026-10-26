import { createGameFixtureServer, gameStates } from './fixtures/game.mjs';
const { origin } = await createGameFixtureServer(Number(process.env.PORT ?? 5186));
console.log(`Synthetic game preview (no live data/providers): ${origin}/__game/lobby`);
console.log(gameStates.map(state => `${origin}/__game/${state}`).join('\n'));
