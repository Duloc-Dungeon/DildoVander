import { describe, expect, it, vi } from 'vitest';
import { createSoundBus } from './wnsSound';

describe('WNS sound bus', () => {
  it('requires explicit opt-in, drops hidden cues and stops on mute', () => {
    const play = vi.fn().mockResolvedValue(undefined); const pause = vi.fn();
    const bus = createSoundBus(() => ({ play, pause, volume: 1, onended: null }));
    expect(bus.local('select', 1000)).toBe(false);
    bus.enable(true);
    expect(bus.local('select', 1000)).toBe(true);
    expect(play).toHaveBeenCalledTimes(1);
    bus.visible(false); expect(pause).toHaveBeenCalledTimes(1);
    expect(bus.local('confirm', 2000)).toBe(false);
    bus.visible(true); expect(play).toHaveBeenCalledTimes(1);
    bus.enable(false); expect(bus.local('confirm', 3000)).toBe(false);
  });
  it('deduplicates approved events and has no private-sensitive audio route', () => {
    const play = vi.fn().mockResolvedValue(undefined);
    const bus = createSoundBus(() => ({ play, pause: vi.fn(), volume: 1, onended: null }));
    bus.enable(true); bus.scope('match');
    const event = { id: 'e1', scopeId: 'match', sequence: 1, projectionRevision: 1, audience: 'participants' as const, type: 'round-reveal', occurredAt: 1000, replay: false };
    expect(bus.projected(event, 1000)).toBe(true);
    expect(bus.projected(event, 1100)).toBe(false);
    expect(bus.projected({ ...event, id: 'secret', sequence: 2, audience: 'self' }, 1200)).toBe(false);
    expect(play).toHaveBeenCalledTimes(1);
  });
});
