/** Integer simulation shared by contract with the server's spark-v1 verifier.
 * See docs/arcade-rules-v1.md. No DOM, randomness, timers or rendering here. */
export const RULES_VERSION = 'spark-v1';
export const FIELD = { width: 360, height: 640, playerY: 480, radius: 7, left: 16, right: 344 } as const;
export const TICK_MS = 1000 / 60;
export const MAX_TICKS = 108000;
export const MAX_TAPS = 24000;
export const PRACTICE_SEED = 0x573253;
export interface Gate { index: number; distance: number; center: number; width: number; height: number }
export interface Collision { x: number; y: number; kind: 'wall' | 'shelf' | 'corridor'; gate: number }
export interface SparkState {
  tick: number; x: number; direction: 1 | -1; score: number;
  status: 'flying' | 'crashed' | 'complete'; collision?: Collision;
}
const clamp = (value: number, min: number, max: number) => Math.max(min, Math.min(max, value));

export function createCourse(seed: number, count = 1201): Gate[] {
  let random = (seed >>> 0) || 1;
  let center = 244;
  return Array.from({ length: count }, (_, index) => {
    if (index > 0) {
      random ^= random << 13; random ^= random >>> 17; random ^= random << 5;
      center = clamp(center + (((random >>> 0) % 7) - 3) * 32, 92, 268);
    }
    const height = index % 7 === 6 ? 72 : 22;
    const width = index === 0 ? 168 : Math.max(height === 72 ? 104 : 88, 152 - Math.floor(index / 5) * 4);
    return { index, distance: 120 + index * 180, center, width, height };
  });
}

export function createSpark(): SparkState {
  return { tick: 0, x: 180, direction: 1, score: 0, status: 'flying' };
}

export function gateY(gate: Gate, tick: number): number { return FIELD.playerY - gate.distance + tick * 2; }

export function contact(x: number, tick: number, gate: Gate): Collision | undefined {
  if (x - FIELD.radius <= FIELD.left || x + FIELD.radius >= FIELD.right) {
    return { x: x < 180 ? FIELD.left : FIELD.right, y: FIELD.playerY, kind: 'wall', gate: gate.index };
  }
  const y = gateY(gate, tick);
  if (Math.abs(y - FIELD.playerY) > gate.height / 2 + FIELD.radius) return;
  const nearestY = clamp(FIELD.playerY, y - gate.height / 2, y + gate.height / 2);
  for (const [left, right] of [[FIELD.left, gate.center - gate.width / 2], [gate.center + gate.width / 2, FIELD.right]]) {
    const nearestX = clamp(x, left!, right!);
    if ((x - nearestX) ** 2 + (FIELD.playerY - nearestY) ** 2 <= FIELD.radius ** 2) {
      return { x: nearestX, y: nearestY, kind: gate.height > 22 ? 'corridor' : 'shelf', gate: gate.index };
    }
  }
}

/** Mutates only the small nonreactive simulation state. Taps precede movement. */
export function stepSpark(state: SparkState, course: readonly Gate[], taps = 0): void {
  if (state.status !== 'flying') return;
  if (taps % 2) state.direction = state.direction === 1 ? -1 : 1;
  state.tick++;
  state.x += state.direction * 2;
  const gate = course[state.score]!;
  state.collision = contact(state.x, state.tick, gate);
  if (state.collision) { state.status = 'crashed'; return; }
  if (state.tick * 2 > gate.distance + gate.height / 2 + FIELD.radius) state.score++;
  if (state.tick >= MAX_TICKS) state.status = 'complete';
}

export function replaySpark(seed: number, endTick: number, taps: readonly number[]): SparkState {
  if (!Number.isInteger(endTick) || endTick < 1 || endTick > MAX_TICKS || taps.length > MAX_TAPS
    || taps.some((tick, i) => !Number.isInteger(tick) || tick < 0 || tick >= endTick || (i > 0 && tick < taps[i - 1]!))) {
    throw new Error('Invalid spark replay');
  }
  const course = createCourse(seed);
  const state = createSpark();
  let input = 0;
  while (state.tick < endTick && state.status === 'flying') {
    let count = 0;
    while (taps[input] === state.tick) { input++; count++; }
    stepSpark(state, course, count);
  }
  if (state.tick !== endTick || state.status === 'flying' || input !== taps.length) throw new Error('Incomplete spark replay');
  return state;
}
