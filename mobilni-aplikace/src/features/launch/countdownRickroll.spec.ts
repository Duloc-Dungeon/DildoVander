import { expect, it, vi } from 'vitest';
import { CountdownRickroll } from './countdownRickroll';
import { withinRickrollWindow } from './rickrollWindow';

it.each([0, 1, 60_000, 899_999])('opens once for an arrival %s ms after opening', delay => {
  const open = vi.fn();
  const countdown = new CountdownRickroll(open);
  const observation = { launchAt: 5000, now: 5000 + delay, remaining: 0, watching: true, waiting: false };
  countdown.observe(observation);
  countdown.reset();
  countdown.observe(observation);
  expect(open).toHaveBeenCalledOnce();
});
it.each([-1, 900_000, 900_001])('does not open outside the window (%s ms)', delay => {
  expect(withinRickrollWindow(5000, 5000 + delay)).toBe(false);
});
it('waits until foreground, and ignores missing or invalid deadlines', () => {
  const open = vi.fn();
  const countdown = new CountdownRickroll(open);
  countdown.observe({ launchAt: 5000, now: 6000, remaining: 0, watching: false, waiting: false });
  expect(open).not.toHaveBeenCalled();
  expect(withinRickrollWindow(null, 6000)).toBe(false);
  expect(withinRickrollWindow(NaN, 6000)).toBe(false);
  countdown.observe({ launchAt: 5000, now: 6000, remaining: 0, watching: true, waiting: false });
  expect(open).toHaveBeenCalledOnce();
});
