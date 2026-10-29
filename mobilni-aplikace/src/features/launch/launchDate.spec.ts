import { describe, expect, it } from 'vitest';
import { formatLaunchDate, parseLaunchDate } from './launchDate';

describe('recruitment opening in Prague', () => {
  it('uses Prague independently of the host timezone and observes summer time', () => {
    expect(parseLaunchDate('2026-10-30T10:00')).toBe(Date.UTC(2026, 9, 30, 9));
    expect(parseLaunchDate('2026-07-01T10:00')).toBe(Date.UTC(2026, 6, 1, 8));
    expect(formatLaunchDate(Date.UTC(2026, 9, 30, 9))).toBe('2026-10-30T10:00');
  });
  it('rejects nonexistent dates and spring daylight saving gaps', () => {
    expect(parseLaunchDate('2026-03-29T02:30')).toBeNull();
    expect(parseLaunchDate('2026-02-30T10:00')).toBeNull();
    expect(parseLaunchDate('invalid')).toBeNull();
  });
});
