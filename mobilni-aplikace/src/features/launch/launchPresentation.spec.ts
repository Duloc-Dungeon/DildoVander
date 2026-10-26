import { describe, expect, it } from 'vitest';
import { VisiblePresentationClock } from './launchPresentation';

describe('visible invitation time', () => {
  it('counts eight visible seconds, pauses in background, and resumes the saved remainder', () => {
    const first = new VisiblePresentationClock();
    first.update(0, true);
    expect(first.update(3000, false)).toBe(3000);
    expect(first.update(90000, false)).toBe(3000);
    const restored = new VisiblePresentationClock(first.elapsed);
    expect(restored.update(100000, true)).toBe(3000);
    expect(restored.update(104999, true)).toBe(7999);
    expect(restored.update(105000, true)).toBe(8000);
    expect(restored.update(200000, true)).toBe(8000);
  });
  it('does not count hidden time before the first visible presentation or reset on polling', () => {
    const clock = new VisiblePresentationClock(1400);
    clock.update(0, false);
    expect(clock.update(30000, true)).toBe(1400);
    expect(clock.update(31000, true)).toBe(2400);
    expect(clock.update(31500, true)).toBe(2900);
  });
});
