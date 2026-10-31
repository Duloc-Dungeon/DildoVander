// Generates replay witnesses with decisions at most every 12 ticks (200ms).
// Shared with the independent C# verifier; no production/autoplay dependency.
import { writeFile } from 'node:fs/promises';
import { contact, createCourse, createSpark, MAX_TICKS, movementAt, replaySpark, RULES_VERSION, scrollAt, stepSpark } from '../src/features/arcade/sparkEngine.ts';

function witness(seed, endTick) {
  const course = createCourse(seed);
  let states = new Map([[180, { x: 180, direction: 1, path: undefined }]]);
  let gateIndex = 0;
  for (let tick = 1; tick <= endTick; tick++) {
    const next = new Map();
    for (const state of states.values()) {
      const directions = (tick - 1) % 12 === 0 ? [state.direction, -state.direction] : [state.direction];
      for (const direction of directions) {
        const x = state.x + direction * movementAt(tick);
        const key = (direction + 1) * 361 + x;
        if (!next.has(key) && !contact(x, tick, course[gateIndex])) next.set(key, { x, direction,
          path: direction === state.direction ? state.path : { tick: tick - 1, previous: state.path } });
      }
    }
    if (!next.size) throw new Error(`Unreachable seed ${seed}, tick ${tick}`);
    states = next;
    const gate = course[gateIndex];
    if (scrollAt(tick) > gate.distance + gate.height / 2 + 7) gateIndex++;
  }
  const taps = [];
  for (let node = states.values().next().value.path; node; node = node.previous) taps.unshift(node.tick);
  return taps;
}
const vectors = [1, 0xffffffff].map(seed => {
  const taps = witness(seed, MAX_TICKS);
  const { score } = replaySpark(seed, MAX_TICKS, taps);
  return { seed, endTick: MAX_TICKS, taps, score };
});
const state = createSpark();
const course = createCourse(1);
const taps = [];
while (state.status === 'flying') {
  const tap = state.tick % 5 === 0;
  if (tap) taps.push(state.tick);
  stepSpark(state, course, tap ? 1 : 0);
}
vectors.push({ seed: 1, endTick: state.tick, taps, score: state.score });
await writeFile(new URL(`../../contracts/fixtures/${RULES_VERSION}.json`, import.meta.url), JSON.stringify({ rulesVersion: RULES_VERSION, vectors }, null, 2) + '\n');
console.log(vectors.map(({ seed, endTick, score, taps }) => ({ seed, endTick, score, inputs: taps.length })));
