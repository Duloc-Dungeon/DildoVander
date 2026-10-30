import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import GameClash from './GameClash.vue';
import type { Round } from './game';

const round: Round = { index: 1, playerChoice: 'strike', opponentChoice: 'shield', winner: 'opponent' };
let wrapper: VueWrapper | undefined;
let intersect: (entries: { isIntersecting: boolean }[]) => void;
let mediaChanged: () => void;
let draw: FrameRequestCallback;
let reduced = false;
const disconnect = vi.fn();
const removeMediaListener = vi.fn();
const context = { clearRect: vi.fn(), setTransform: vi.fn(), beginPath: vi.fn(), arc: vi.fn(), fill: vi.fn(), fillRect: vi.fn(), moveTo: vi.fn(), lineTo: vi.fn(), stroke: vi.fn(), createRadialGradient: vi.fn(() => ({ addColorStop: vi.fn() })) };

beforeEach(() => {
  reduced = false;
  vi.clearAllMocks();
  vi.spyOn(performance, 'now').mockReturnValue(100);
  vi.stubGlobal('requestAnimationFrame', vi.fn(callback => { draw = callback; return 1; }));
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
  vi.stubGlobal('IntersectionObserver', class {
    constructor(callback: typeof intersect) { intersect = callback; }
    observe() {}
    disconnect = disconnect;
  });
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect = disconnect; });
  vi.spyOn(window, 'matchMedia').mockImplementation(() => ({
    get matches() { return reduced; },
    addEventListener: (_event: string, callback: () => void) => { mediaChanged = callback; },
    removeEventListener: removeMediaListener,
  }) as unknown as MediaQueryList);
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation((() => context) as unknown as typeof HTMLCanvasElement.prototype.getContext);
  vi.spyOn(HTMLCanvasElement.prototype, 'getBoundingClientRect').mockReturnValue({ width: 350, height: 156 } as DOMRect);
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe('published spell clash', () => {
  it('uses a labelled generic wand only for practice and keeps the fallback on image failure', async () => {
    wrapper = mount(GameClash, { props: { practiceOpponent: true, opponentWandUrl: 'blob:stale-match' } });
    const opponent = () => wrapper!.get('.game-magic-clash__origin--opponent');
    expect(opponent().get('img').attributes('src')).toContain('wand-violet');
    expect(wrapper.text()).toContain('Cvičná hůlka');
    await opponent().get('img').trigger('error');
    expect(opponent().find('svg').exists()).toBe(true);
    await wrapper.setProps({ practiceOpponent: false, opponentWandUrl: 'blob:real-opponent' });
    expect(opponent().get('img').attributes('src')).toBe('blob:real-opponent');
    expect(opponent().get('img').classes()).not.toContain('game-magic-clash__artifact--practice');
  });
  it('keeps a loaded result static and never replays the same round on polling', async () => {
    wrapper = mount(GameClash, { props: { round, theatrical: true } });
    intersect([{ isIntersecting: true }]);
    await wrapper.setProps({ round: { ...round } });
    expect(requestAnimationFrame).not.toHaveBeenCalled();
    expect(wrapper.findAll('.game-magic-clash__spells img')).toHaveLength(2);
    expect(wrapper.attributes('data-winner')).toBe('opponent');
    expect(wrapper.attributes('data-motion')).toBe('still');
  });

  it('lights a side only after both choices have been published', async () => {
    wrapper = mount(GameClash, { props: { theatrical: true, locked: true } });
    intersect([{ isIntersecting: true }]);
    expect(wrapper.attributes('data-winner')).toBeUndefined();
    await wrapper.setProps({ locked: false, round: { ...round, opponentChoice: 'hidden' } });
    expect(wrapper.attributes('data-winner')).toBeUndefined();
    expect(requestAnimationFrame).not.toHaveBeenCalled();
    await wrapper.setProps({ round: { ...round, index: 2 } });
    await flushPromises();
    expect(wrapper.attributes('data-winner')).toBe('opponent');
    expect(wrapper.attributes('data-motion')).toBe('playing');
    expect(requestAnimationFrame).toHaveBeenCalledTimes(1);
  });

  it('animates one newly published round and settles within two seconds', async () => {
    wrapper = mount(GameClash);
    intersect([{ isIntersecting: true }]);
    await wrapper.setProps({ round });
    await flushPromises();
    expect(wrapper.attributes('data-motion')).toBe('playing');
    draw(600);
    expect(context.arc).toHaveBeenCalled();
    draw(2101);
    await wrapper.vm.$nextTick();
    expect(wrapper.attributes('data-motion')).toBe('still');
    expect(cancelAnimationFrame).toHaveBeenCalled();
  });

  it('defers an offscreen reveal and cancels when the scene leaves the viewport', async () => {
    wrapper = mount(GameClash);
    await wrapper.setProps({ round });
    expect(requestAnimationFrame).not.toHaveBeenCalled();
    intersect([{ isIntersecting: true }]);
    await wrapper.vm.$nextTick();
    expect(wrapper.attributes('data-motion')).toBe('playing');
    intersect([{ isIntersecting: false }]);
    await wrapper.vm.$nextTick();
    expect(wrapper.attributes('data-motion')).toBe('still');
  });

  it('honors reduced motion immediately and when the setting changes', async () => {
    reduced = true;
    wrapper = mount(GameClash);
    intersect([{ isIntersecting: true }]);
    await wrapper.setProps({ round });
    expect(requestAnimationFrame).not.toHaveBeenCalled();
    reduced = false;
    await wrapper.setProps({ round: { ...round, index: 2 } });
    await flushPromises();
    expect(wrapper.attributes('data-motion')).toBe('playing');
    reduced = true;
    mediaChanged();
    await wrapper.vm.$nextTick();
    expect(wrapper.attributes('data-motion')).toBe('still');
  });

  it('does not expose a spell before reveal and cleans up observers on unmount', () => {
    wrapper = mount(GameClash, { props: { locked: true } });
    expect(wrapper.classes()).toContain('game-clash-stage--locked');
    expect(wrapper.text()).toContain('Čekáme na soupeře');
    expect(wrapper.find('.game-magic-clash__spells').exists()).toBe(false);
    wrapper.unmount(); wrapper = undefined;
    expect(disconnect).toHaveBeenCalledTimes(2);
    expect(removeMediaListener).toHaveBeenCalledWith('change', mediaChanged);
  });

  it('keeps the static scene when canvas rendering is unavailable', async () => {
    vi.mocked(HTMLCanvasElement.prototype.getContext).mockReturnValue(null);
    wrapper = mount(GameClash);
    intersect([{ isIntersecting: true }]);
    await wrapper.setProps({ round });
    await flushPromises();
    expect(requestAnimationFrame).not.toHaveBeenCalled();
    expect(wrapper.findAll('.game-magic-clash__spells img')).toHaveLength(2);
  });

  it('cancels active and queued effects when the document is hidden', async () => {
    wrapper = mount(GameClash);
    intersect([{ isIntersecting: true }]);
    await wrapper.setProps({ round });
    await flushPromises();
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(true);
    document.dispatchEvent(new Event('visibilitychange'));
    await wrapper.vm.$nextTick();
    expect(wrapper.attributes('data-motion')).toBe('still');
    intersect([{ isIntersecting: false }]);
    await wrapper.setProps({ round: { ...round, index: 2 } });
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    vi.mocked(requestAnimationFrame).mockClear();
    intersect([{ isIntersecting: true }]);
    expect(requestAnimationFrame).not.toHaveBeenCalled();
  });
});
