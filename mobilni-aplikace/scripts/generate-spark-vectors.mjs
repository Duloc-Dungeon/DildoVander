// Generates replay witnesses with decisions at most every 12 ticks (200ms).
// Shared with the independent C# verifier; no production/autoplay dependency.
import { writeFile } from 'node:fs/promises';
import { contact, createCourse, MAX_TICKS, replaySpark } from '../src/features/arcade/sparkEngine.ts';

function witness(seed, endTick) {
  const course = createCourse(seed);
  let states = new Map([[180, { x: 180, direction: 1, path: undefined }]]);
  let gateIndex = 0;
  for (let tick = 1; tick <= endTick; tick++) {
    const next = new Map();
    for (const state of states.values()) {
      const directions = (tick - 1) % 12 === 0 ? [state.direction, -state.direction] : [state.direction];
      for (const direction of directions) {
        const x = state.x + direction * 2;
        const key = (direction + 1) * 361 + x;
        if (!next.has(key) && !contact(x, tick, course[gateIndex])) next.set(key, { x, direction,
          path: direction === state.direction ? state.path : { tick: tick - 1, previous: state.path } });
      }
    }
    if (!next.size) throw new Error(`Unreachable seed ${seed}, tick ${tick}`);
    states = next;
    const gate = course[gateIndex];
    if (tick * 2 > gate.distance + gate.height / 2 + 7) gateIndex++;
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
vectors.push({ seed: 1, endTick: 321, taps: Array.from({ length: 65 }, (_, i) => i * 5), score: 3 });
await writeFile(new URL('../../contracts/fixtures/spark-v1.json', import.meta.url), JSON.stringify({ rulesVersion: 'spark-v1', vectors }, null, 2) + '\n');
console.log(vectors.map(({ seed, endTick, score, taps }) => ({ seed, endTick, score, inputs: taps.length })));
