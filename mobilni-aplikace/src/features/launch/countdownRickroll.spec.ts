import { expect, it, vi } from 'vitest';
import { CountdownRickroll } from './countdownRickroll';

const waiting = { launchAt: 5_000, remaining: 1_000, now: 4_000, watching: true, waiting: true };
const zero = { ...waiting, remaining: 0, now: 5_000 };

it('opens automatically once when an observed countdown reaches zero', () => {
  const open = vi.fn();
  const countdown = new CountdownRickroll(open);
  countdown.observe(waiting);
  expect(open).not.toHaveBeenCalled();
  countdown.observe(zero);
  countdown.observe({ ...zero, now: 6_000 });
  expect(open).toHaveBeenCalledOnce();
});

it.each([
  ['late arrival', undefined, zero],
  ['unscheduled countdown', waiting, { ...zero, launchAt: null }],
  ['rescheduled countdown', waiting, { ...zero, launchAt: 4_500 }],
  ['background page', waiting, { ...zero, watching: false }],
  ['suspended page', waiting, { ...zero, now: 60_000 }],
  ['already admitted visitor', { ...waiting, waiting: false }, zero],
] as const)('skips the surprise for a %s', (_name, previous, current) => {
  const open = vi.fn();
  const countdown = new CountdownRickroll(open);
  if (previous) countdown.observe(previous);
  countdown.observe(current);
  expect(open).not.toHaveBeenCalled();
});

it('does not replay after returning from the background or restarting', () => {
  const open = vi.fn();
  const countdown = new CountdownRickroll(open);
  countdown.observe(waiting);
  countdown.reset();
  countdown.observe(zero);
  new CountdownRickroll(open).observe(zero);
  expect(open).not.toHaveBeenCalled();
});

it('still detects zero when a policy refresh confirms that access has opened', () => {
  const open = vi.fn();
  const countdown = new CountdownRickroll(open);
  countdown.observe(waiting);
  countdown.observe({ ...zero, waiting: false });
  expect(open).toHaveBeenCalledOnce();
});
