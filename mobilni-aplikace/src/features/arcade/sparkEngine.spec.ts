import { describe, expect, it } from 'vitest';
import { contact, createCourse, createSpark, gateCenterAt, MAX_TICKS, replaySpark, scrollAt, stepSpark, type Gate } from './sparkEngine';
import { witness, witnessFrom } from '../../../scripts/spark-witness.mjs';
import sharedVectors from '../../../../contracts/fixtures/spark-v3.json';

describe('spark-v3 deterministic progression', () => {
  it('introduces patterns, motion, echoes, pairs, opposing pairs and moving corridors with recovery', () => {
    const course = createCourse(1);
    expect(course[0]).toMatchObject({ center: 244, width: 168, height: 22 });
    expect(course.slice(1, 4).map(gate => gate.center)).toEqual([132, 180, 228]);
    expect(course[13]!.pattern).toBe('s');
    expect(course[25]!.pattern).toBe('sleeve');
    expect(course.slice(0, 15).some(gate => gate.motion)).toBe(false);
    expect(course[16]!.motion?.amplitude).toBe(12);
    expect(course[16]!.width).toBeGreaterThan(course[17]!.width - 1);
    expect(course[30]!.echoTarget).toBe(32);
    expect(course[32]).toMatchObject({ width: 264, echoSource: 30 });
    expect(course[40]!.motion).toEqual(course[41]!.motion);
    expect(course[88]!.motion!.sign).toBe(-course[89]!.motion!.sign);
    expect(course[99]).toMatchObject({ height: 72, motion: { amplitude: 24 } });
    expect(course[102]!.echoTarget).toBe(104);
    expect(course[103]!.echoTarget).toBe(105);
    for (const gate of course.filter(gate => (gate.index - 1) % 12 >= 9)) {
      expect(gate.width).toBeGreaterThanOrEqual(160);
      expect(gate.motion).toBeUndefined();
      expect(gate.echoTarget).toBeUndefined();
    }
  });
  it('keeps every moving opening in bounds with exact periodic integer geometry', () => {
    const violations: string[] = [];
    for (const gate of createCourse(0xffffffff).filter(gate => gate.motion)) {
      for (let tick = 0; tick < 192; tick++) {
        const center = gateCenterAt(gate, tick);
        if (!Number.isInteger(center) || center - gate.width / 2 < 16 || center + gate.width / 2 > 344
          || gateCenterAt(gate, tick + 192) !== center || Math.abs(center - gateCenterAt(gate, tick + 1)) > 1) {
          violations.push(`gate ${gate.index}, tick ${tick}, center ${center}`);
        }
      }
    }
    expect(violations).toEqual([]);
    const first = createCourse(1)[16]!;
    const arrival = Math.ceil(first.distance * 5 / 11);
    const centers = Array.from({ length: 19 }, (_, offset) => gateCenterAt(first, arrival - 9 + offset));
    expect(centers).toEqual([...centers].sort((a, b) => a - b));
  });
  it('agrees with shared complete and terminal replay witnesses', () => {
    const collisions = new Set();
    for (const vector of sharedVectors.vectors) {
      const state = replaySpark(vector.seed, vector.endTick, vector.taps);
      expect(state.score).toBe(vector.score);
      expect(state.status).toBe(vector.endTick === MAX_TICKS ? 'complete' : 'crashed');
      if (state.collision) collisions.add(state.collision.kind);
    }
    expect([...collisions].sort()).toEqual(['corridor', 'echo', 'shelf', 'wall']);
  });
  it('uses unsigned seeds and preserves integer V2 timing and tap parity', () => {
    expect(createCourse(0)).toEqual(createCourse(1));
    expect(createCourse(0xffffffff)).not.toEqual(createCourse(1));
    const state = createSpark(); const course = createCourse(1);
    stepSpark(state, course, 2); expect(state.x).toBe(182);
    stepSpark(state, course, 1); expect(state.x).toBe(180);
    expect(scrollAt(10)).toBe(22);
    expect(replaySpark(1, 72, [])).toMatchObject({ score: 1, collision: { kind: 'wall', x: 344 } });
  });
  it('tests circle corners and uses the moving gap for collision', () => {
    const gate = createCourse(1)[0]!;
    expect(contact(166, 48, gate)).toBeUndefined();
    expect(contact(164, 48, gate)?.kind).toBe('shelf');
    const moving: Gate = { index: 0, distance: 110, center: 180, width: 100, height: 22,
      motion: { amplitude: 24, phase: 46, sign: 1 } };
    expect(contact(132, 50, moving)?.kind).toBe('shelf');
    expect(contact(235, 50, moving)).toBeUndefined();
  });
  it('checks a second overlapping gate before the first has cleared', () => {
    const course: Gate[] = [
      { index: 0, distance: 100, center: 180, width: 200, height: 72 },
      { index: 1, distance: 110, center: 280, width: 80, height: 22 },
    ];
    const state = { ...createSpark(), tick: 49 };
    stepSpark(state, course);
    expect(state).toMatchObject({ score: 0, status: 'crashed', collision: { gate: 1 } });
  });
  it('records exact x once, immediately announces it, preserves it and isolates runs', () => {
    const course: Gate[] = [
      { index: 0, distance: 2, center: 180, width: 200, height: 22, echoTarget: 2 },
      { index: 1, distance: 182, center: 180, width: 200, height: 22 },
      { index: 2, distance: 362, center: 180, width: 264, height: 22, echoSource: 0 },
    ];
    const before = JSON.stringify(course);
    const state = createSpark(); stepSpark(state, course);
    expect(state.echoes).toEqual([{ source: 0, target: 2, x: 182, halfWidth: 8 }]);
    stepSpark(state, course, 1);
    expect(state.echoes[0]!.x).toBe(182);
    const other = createSpark(); stepSpark(other, course, 1);
    expect(other.echoes[0]!.x).toBe(178);
    expect(JSON.stringify(course)).toBe(before);
    expect(contact(182, 164, course[2]!, state.echoes)?.kind).toBe('echo');
    expect(contact(100, 164, course[2]!, state.echoes)).toBeUndefined();
  });
  it('rejects early stops, simulation after death, unused and invalid taps', () => {
    for (const [tick, taps] of [[20, []], [73, []], [72, [72]], [72, [-1]], [72, [2, 1]], [72, [.5]], [MAX_TICKS + 1, []]] as [number, number[]][]) {
      expect(() => replaySpark(1, tick, taps)).toThrow();
    }
  });
  it('does not reward fixed-frequency tapping through learned patterns', () => {
    let maximum = 0;
    for (const seed of [1, 7, 219, 0xffffffff, 0x573253]) {
      for (const interval of [1, 2, 3, 4, 5, 6, 8, 10, 12]) {
        const course = createCourse(seed); const state = createSpark();
        while (state.status === 'flying' && state.score < 100) stepSpark(state, course, state.tick % interval === 0 ? 1 : 0);
        maximum = Math.max(maximum, state.score);
      }
    }
    // Wide teaching patterns tolerate centered tapping; the first echo forces
    // a different trajectory before paired-motion progression begins.
    expect(maximum).toBeLessThan(35);
  });
  it('carries position, direction and both echoes through 300 connected gates across seeds', () => {
    for (const seed of [7, 219, 0x573253, 42, 999]) {
      const taps = witness(seed, 25000, 256);
      const course = createCourse(seed); const state = createSpark(); let input = 0;
      let sawTwoEchoes = false;
      while (state.tick < 25000 && state.status === 'flying') {
        const tap = taps[input] === state.tick;
        if (tap) input++;
        stepSpark(state, course, tap ? 1 : 0);
        sawTwoEchoes ||= state.echoes.length === 2;
      }
      expect(state.status).toBe('flying');
      expect(state.score).toBeGreaterThan(300);
      expect(sawTwoEchoes).toBe(true);
    }
  }, 60000);
  it('leaves a route for every allowed integer imprint and either outgoing direction', () => {
    const course = createCourse(1);
    for (const sourceIndex of [30, 42, 102]) {
      const source = course[sourceIndex]!;
      const arrival = Math.ceil(source.distance * 5 / 11);
      const endTick = Math.ceil((course[sourceIndex + 3]!.distance + 44) * 5 / 11);
      for (let x = source.center - source.width / 2 + 8; x < source.center + source.width / 2 - 7; x++) {
        for (const direction of [-1, 1] as const) {
          const initial = { ...createSpark(), x, direction, tick: arrival, score: sourceIndex,
            echoes: [{ source: sourceIndex, target: sourceIndex + 2, x, halfWidth: sourceIndex < 40 ? 8 : 12 }] };
          expect(() => witnessFrom(course, initial, endTick, 64), `source ${sourceIndex}, x ${x}, direction ${direction}`).not.toThrow();
        }
      }
    }
  }, 60000);
});
