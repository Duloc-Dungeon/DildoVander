import { describe, expect, it } from 'vitest';
import { contact, createCourse, createSpark, MAX_TICKS, movementAt, replaySpark, scrollAt, stepSpark } from './sparkEngine';
import sharedVectors from '../../../../contracts/fixtures/spark-v2.json';

describe('spark-v2 deterministic physics', () => {
  it('slightly narrows ordinary gates while preserving the opening and long corridors', () => {
    const course = createCourse(1);
    expect(course[0]!.width).toBe(168);
    expect(course[1]!.width).toBe(148);
    expect(course[80]!.width).toBe(84);
    expect(course[6]!.width).toBe(148);
    expect(course[83]!.width).toBe(104);
  });
  it('agrees with shared maximum-duration vectors including every long corridor', () => {
    for (const vector of sharedVectors.vectors) expect(replaySpark(vector.seed, vector.endTick, vector.taps).score).toBe(vector.score);
  });
  it('matches the independent C# replay golden fixture', () => {
    const taps = Array.from({ length: 59 }, (_, i) => i * 5);
    expect(replaySpark(1, 292, taps)).toMatchObject({ score: 3, tick: 292, status: 'crashed' });
  });
  it('moves and scrolls 10% faster with integer steps', () => {
    const state = createSpark(); const course = createCourse(1);
    for (let tick = 0; tick < 10; tick++) stepSpark(state, course);
    expect(state.x).toBe(202);
    expect(scrollAt(10)).toBe(22);
  });
  it('uses unsigned generator values and consistent zero seeds', () => {
    expect(createCourse(0)).toEqual(createCourse(1));
    expect(createCourse(0xffffffff, 3)).toEqual([
      { index: 0, distance: 120, center: 244, width: 168, height: 22 },
      ...createCourse(0xffffffff).slice(1, 3),
    ]);
    expect(createCourse(0xffffffff)).not.toEqual(createCourse(1));
  });
  it('awards the first easy gate then makes an untended spark hit the wall', () => {
    const state = createSpark(); const course = createCourse(1);
    while (state.status === 'flying') stepSpark(state, course);
    expect(state).toMatchObject({ score: 1, tick: 72, collision: { kind: 'wall', x: 344 } });
    expect(replaySpark(1, 72, []).score).toBe(1);
  });
  it('tests the circle itself at a shelf corner, not the decorative glow or its bounding square', () => {
    const gate = createCourse(1)[0]!;
    // Gate corner (160, 476), spark center (166,480): outside r=7 despite overlapping squares.
    expect(contact(166, 48, gate)).toBeUndefined();
    expect(contact(164, 48, gate)?.kind).toBe('shelf');
  });
  it('does not ignore multiple inputs in the same tick', () => {
    const course = createCourse(1); const state = createSpark();
    stepSpark(state, course, 2); expect(state.x).toBe(182);
    stepSpark(state, course, 1); expect(state.x).toBe(180);
  });
  it('rejects early stops, simulation after death, unused and invalid taps', () => {
    for (const [tick, taps] of [[20, []], [73, []], [72, [72]], [72, [-1]], [72, [2, 1]], [72, [.5]], [MAX_TICKS + 1, []]] as [number, number[]][]) {
      expect(() => replaySpark(1, tick, taps)).toThrow();
    }
  });
  it('rapid fixed-frequency tapping does not farm a high score', () => {
    let maximum = 0;
    for (const seed of [1, 7, 219, 0xffffffff, 0x573253]) {
      for (const interval of [1, 2, 3, 4, 5, 6, 8, 10, 12]) {
        const course = createCourse(seed); const state = createSpark();
        while (state.status === 'flying' && state.score < 100) stepSpark(state, course, state.tick % interval === 0 ? 1 : 0);
        maximum = Math.max(maximum, state.score);
      }
    }
    expect(maximum).toBeLessThan(15);
  });
  it('has an unbroken reachable trajectory with decisions no faster than every 200ms', () => {
    // Exhaustive reachable-state propagation over the first 300 gates, including
    // the minimum gap and repeated long corridors. This tests whole sequences.
    for (const seed of [1, 7, 219, 0xffffffff, 0x573253, 42, 999]) {
      const course = createCourse(seed);
      let states = new Map([[361 + 180, { x: 180, direction: 1 }]]);
      let gateIndex = 0;
      for (let tick = 1; gateIndex < 300; tick++) {
        const next = new Map<number, { x: number; direction: number }>();
        for (const state of states.values()) {
          const directions = (tick - 1) % 12 === 0 ? [state.direction, -state.direction] : [state.direction];
          for (const direction of directions) {
            const x = state.x + direction * movementAt(tick);
            if (!contact(x, tick, course[gateIndex]!)) next.set((direction + 1) * 361 + x, { x, direction });
          }
        }
        expect(next.size, `seed ${seed}, tick ${tick}, gate ${gateIndex}`).toBeGreaterThan(0);
        states = next;
        const gate = course[gateIndex]!;
        if (scrollAt(tick) > gate.distance + gate.height / 2 + 7) gateIndex++;
      }
    }
  }, 20000);
});
