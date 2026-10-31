/** Integer simulation shared by contract with the independent C# verifier.
 * See docs/arcade-rules-v3.md. No DOM, randomness, timers or rendering here. */
export const RULES_VERSION = 'spark-v3';
export const FIELD = { width: 360, height: 640, playerY: 480, radius: 7, left: 16, right: 344 } as const;
export const TICK_MS = 1000 / 60;
export const MAX_TICKS = 108000;
export const MAX_GATES = 1321;
export const MAX_TAPS = 24000;
export const PRACTICE_SEED = 0x573253;
export interface Gate {
  index: number; distance: number; center: number; width: number; height: number;
  pattern?: 'stairs' | 's' | 'sleeve';
  motion?: { amplitude: number; phase: number; sign: number };
  echoTarget?: number;
  echoSource?: number;
}
export interface Echo { source: number; target: number; x: number; halfWidth: number }
export interface Collision { x: number; y: number; kind: 'wall' | 'shelf' | 'corridor' | 'echo'; gate: number }
export interface SparkState {
  tick: number; x: number; direction: 1 | -1; score: number; echoes: Echo[];
  status: 'flying' | 'crashed' | 'complete'; collision?: Collision;
}
const clamp = (value: number, min: number, max: number) => Math.max(min, Math.min(max, value));
const modulo = (value: number, base: number) => ((value % base) + base) % base;
const patterns = [[-48, 0, 48], [-48, 48, -48], [0, 48, 0]] as const;
const patternNames = ['stairs', 's', 'sleeve'] as const;

export function createCourse(seed: number, count = MAX_GATES): Gate[] {
  let random = (seed >>> 0) || 1;
  const course: Gate[] = [];
  for (let index = 0; index < count; index++) {
    const gate: Gate = { index, distance: 120 + index * 180, center: 244, width: 168, height: 22 };
    if (index === 0) { course.push(gate); continue; }
    const block = Math.floor((index - 1) / 12);
    const slot = (index - 1) % 12;
    if (slot === 0) { random ^= random << 13; random ^= random >>> 17; random ^= random << 5; }
    const pattern = block < 3 ? block % 3 : (random >>> 0) % 3;
    const mirror = index >= 35 && (random & 1) ? -1 : 1;
    gate.center = 180;
    if (slot < 3) {
      gate.pattern = patternNames[pattern]!;
      gate.center += patterns[pattern]![slot]! * mirror;
      if ((pattern === 1 && slot === 2) || (pattern === 2 && slot === 0)) gate.height = 72;
    }
    if (index >= 60 && slot === 3) gate.center -= 48 * mirror;
    if (index >= 90 && slot === 2) gate.height = 72;
    gate.width = gate.height === 72
      ? Math.max(132, 172 - Math.floor(index / 12) * 4)
      : Math.max(104, 164 - Math.floor(index / 12) * 4);
    if (slot >= 9) gate.width = Math.max(160, gate.width);
    if ((index >= 15 && slot === 3) || (index >= 35 && slot === 4) || (index >= 90 && slot < 3)) {
      const anchorDistance = 120 + (block * 12 + 4) * 180;
      gate.motion = {
        amplitude: index < 35 ? 12 : index < 90 ? 18 : 24,
        phase: modulo(48 - Math.floor(anchorDistance * 5 / 11), 192),
        sign: index >= 60 && slot === 4 && block % 2 === 1 ? -1 : 1,
      };
      gate.width = Math.max(gate.width, index < 35 || gate.height === 72 ? 156 : 140);
    }
    if ((index >= 20 && slot === 5) || (index >= 100 && slot === 6)) {
      gate.echoTarget = index + 2;
      gate.width = Math.max(148, gate.width);
    }
    const source = index - 2;
    if ((source >= 20 && slot === 7) || (source >= 100 && slot === 8)) {
      gate.echoSource = source;
      gate.width = 264;
    }
    course.push(gate);
  }
  return course;
}

export function createSpark(): SparkState {
  return { tick: 0, x: 180, direction: 1, score: 0, echoes: [], status: 'flying' };
}
export function movementAt(tick: number): number { return tick % 5 === 0 ? 3 : 2; }
export function scrollAt(tick: number): number { return tick * 2 + Math.floor(tick / 5); }
export function gateY(gate: Gate, tick: number): number { return FIELD.playerY - gate.distance + scrollAt(tick); }
export function gateCenterAt(gate: Gate, tick: number): number {
  if (!gate.motion) return gate.center;
  const { amplitude, phase, sign } = gate.motion;
  const position = modulo(tick + phase, 192);
  return gate.center + sign * (-amplitude + Math.floor(Math.min(position, 192 - position) * 2 * amplitude / 96));
}

function rectangleContact(x: number, y: number, left: number, right: number, height: number) {
  const nearestX = clamp(x, left, right);
  const nearestY = clamp(FIELD.playerY, y - height / 2, y + height / 2);
  if ((x - nearestX) ** 2 + (FIELD.playerY - nearestY) ** 2 <= FIELD.radius ** 2) {
    return { x: nearestX, y: nearestY };
  }
}

export function contact(x: number, tick: number, gate: Gate, echoes: readonly Echo[] = []): Collision | undefined {
  if (x - FIELD.radius <= FIELD.left || x + FIELD.radius >= FIELD.right) {
    return { x: x < 180 ? FIELD.left : FIELD.right, y: FIELD.playerY, kind: 'wall', gate: gate.index };
  }
  const y = gateY(gate, tick);
  if (Math.abs(y - FIELD.playerY) > gate.height / 2 + FIELD.radius) return;
  const center = gateCenterAt(gate, tick);
  for (const [left, right] of [[FIELD.left, center - gate.width / 2], [center + gate.width / 2, FIELD.right]]) {
    const point = rectangleContact(x, y, left!, right!, gate.height);
    if (point) return { ...point, kind: gate.height > 22 ? 'corridor' : 'shelf', gate: gate.index };
  }
  const echo = echoes.find(value => value.target === gate.index);
  if (echo) {
    const point = rectangleContact(x, y, echo.x - echo.halfWidth, echo.x + echo.halfWidth, gate.height);
    if (point) return { ...point, kind: 'echo', gate: gate.index };
  }
}

/** Taps precede movement. Course stays immutable; each run owns its announced echoes. */
export function stepSpark(state: SparkState, course: readonly Gate[], taps = 0): void {
  if (state.status !== 'flying') return;
  if (taps % 2) state.direction = state.direction === 1 ? -1 : 1;
  state.tick++;
  state.x += state.direction * movementAt(state.tick);
  const scroll = scrollAt(state.tick);
  if (state.x - FIELD.radius <= FIELD.left || state.x + FIELD.radius >= FIELD.right) {
    state.collision = { x: state.x < 180 ? FIELD.left : FIELD.right, y: FIELD.playerY, kind: 'wall', gate: state.score };
    state.status = 'crashed';
    return;
  }
  // Generated gates are ordered and at most 72 high. Check every candidate,
  // including a later gate while the first relevant corridor remains uncleared.
  for (let i = state.score; i < course.length; i++) {
    const gate = course[i]!;
    if (gate.distance - 36 - FIELD.radius > scroll) break;
    state.collision = contact(state.x, state.tick, gate, state.echoes);
    if (state.collision) { state.status = 'crashed'; return; }
  }
  const previousScroll = scrollAt(state.tick - 1);
  for (let i = state.score; i < course.length; i++) {
    const gate = course[i]!;
    if (gate.distance > scroll) break;
    if (gate.echoTarget !== undefined && previousScroll < gate.distance) {
      state.echoes.push({ source: gate.index, target: gate.echoTarget, x: state.x, halfWidth: gate.index < 40 ? 8 : 12 });
    }
  }
  while (state.score < course.length) {
    const gate = course[state.score]!;
    if (scroll <= gate.distance + gate.height / 2 + FIELD.radius) break;
    state.score++;
  }
  state.echoes = state.echoes.filter(echo => echo.target >= state.score);
  if (state.tick >= MAX_TICKS || state.score === course.length) state.status = 'complete';
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
