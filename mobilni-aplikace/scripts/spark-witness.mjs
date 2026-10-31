// Offline feasibility search only; never imported by the application.
// State identity includes active imprints: merging by x/direction alone would
// silently combine incompatible futures and produce a false witness.
import { createCourse, createSpark, MAX_TICKS, stepSpark } from '../src/features/arcade/sparkEngine.ts';

export function witness(seed, endTick = MAX_TICKS, capacity = 512) {
  return witnessFrom(createCourse(seed), createSpark(), endTick, capacity);
}

export function witnessFrom(course, initial, endTick, capacity = 512) {
  let states = [{ state: initial, path: undefined }];
  for (let tick = initial.tick; tick < endTick; tick += 12) {
    const next = new Map();
    for (const node of states) {
      for (const tap of [0, 1]) {
        const state = { ...node.state, echoes: [...node.state.echoes] };
        for (let offset = 0; offset < Math.min(12, endTick - tick); offset++) {
          stepSpark(state, course, offset === 0 ? tap : 0);
          if (state.status === 'crashed') break;
        }
        if (state.status === 'crashed') continue;
        const key = `${state.x}:${state.direction}:${state.echoes.map(echo => `${echo.target}/${echo.x}`).join(',')}`;
        if (!next.has(key)) next.set(key, { state, path: tap ? { tick, previous: node.path } : node.path });
      }
    }
    if (!next.size) throw new Error(`No witness found: tick ${tick}, capacity ${capacity}`);
    const candidates = [...next.values()];
    // Bound memory without pretending this beam search is an exhaustive proof.
    states = candidates.length <= capacity ? candidates
      : Array.from({ length: capacity }, (_, index) => candidates[Math.floor(index * candidates.length / capacity)]);
  }
  const taps = [];
  for (let node = states[0].path; node; node = node.previous) taps.push(node.tick);
  return taps.reverse();
}
