// Regenerates V3 only. Existing V1/V2 fixtures remain immutable compatibility evidence.
import { writeFile } from 'node:fs/promises';
import { createCourse, createSpark, MAX_TICKS, replaySpark, RULES_VERSION, stepSpark } from '../src/features/arcade/sparkEngine.ts';
import { witness } from './spark-witness.mjs';

const vectors = [1, 0xffffffff].map(seed => {
  const taps = witness(seed);
  const { score } = replaySpark(seed, MAX_TICKS, taps);
  return { seed, endTick: MAX_TICKS, taps, score };
});
// Truncate a successful input sequence at each mechanic and continue to an actual
// collision. These distinguish timing, moving corridors and announced seals.
for (const stopScore of [0, 16, 32, 65, 96, 104]) {
  const state = createSpark();
  const course = createCourse(1);
  const path = vectors[0].taps;
  const taps = [];
  let input = 0;
  let stopped = false;
  while (state.status === 'flying') {
    stopped ||= state.score >= stopScore;
    const tap = !stopped && path[input] === state.tick;
    if (tap) { taps.push(state.tick); input++; }
    stepSpark(state, course, tap ? 1 : 0);
  }
  vectors.push({ seed: 1, endTick: state.tick, taps, score: state.score });
}

// Force real terminal contacts with the new hazards, so a server that ignores
// seals or moving long passages cannot pass only successful survival fixtures.
const course = createCourse(1);
const path = vectors[0].taps;
const snapshots = new Map();
const reference = createSpark();
let input = 0;
while (reference.score < 107) {
  snapshots.set(reference.tick, { ...reference, echoes: [...reference.echoes] });
  const tap = path[input] === reference.tick;
  if (tap) input++;
  stepSpark(reference, course, tap ? 1 : 0);
}
for (const [gate, kind] of [[32, 'echo'], [99, 'corridor'], [104, 'echo']]) {
  const arrival = Math.ceil(course[gate].distance * 5 / 11);
  let terminal;
  for (let changedTick = arrival - 90; changedTick <= arrival && !terminal; changedTick++) {
    const snapshot = snapshots.get(changedTick);
    const state = { ...snapshot, echoes: [...snapshot.echoes] };
    const taps = [...path.filter(tick => tick < arrival + 60), changedTick].sort((a, b) => a - b);
    let next = taps.findIndex(tick => tick >= changedTick);
    while (state.status === 'flying' && state.tick < arrival + 60) {
      let count = 0;
      while (taps[next] === state.tick) { next++; count++; }
      stepSpark(state, course, count);
    }
    if (state.collision?.gate === gate && state.collision.kind === kind) {
      terminal = { seed: 1, endTick: state.tick, taps: taps.filter(tick => tick < state.tick), score: state.score };
    }
  }
  if (!terminal) throw new Error(`No ${kind} contact witness for gate ${gate}`);
  replaySpark(terminal.seed, terminal.endTick, terminal.taps);
  vectors.push(terminal);
}
await writeFile(new URL(`../../contracts/fixtures/${RULES_VERSION}.json`, import.meta.url), JSON.stringify({ rulesVersion: RULES_VERSION, vectors }, null, 2) + '\n');
console.log(vectors.map(({ seed, endTick, score, taps }) => ({ seed, endTick, score, inputs: taps.length })));
