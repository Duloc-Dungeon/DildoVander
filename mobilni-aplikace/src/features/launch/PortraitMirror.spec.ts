import { mount, flushPromises } from '@vue/test-utils';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import PortraitMirror from '../../components/scene/PortraitMirror.vue';

const { animate, controls } = vi.hoisted(() => {
  const controls: Array<{ play: ReturnType<typeof vi.fn>; pause: ReturnType<typeof vi.fn>; complete: ReturnType<typeof vi.fn>; finish: () => void }> = [];
  return {
    controls,
    animate: vi.fn(() => {
      let finish!: () => void;
      const promise = new Promise<void>(resolve => { finish = resolve; });
      const playback = { play: vi.fn(), pause: vi.fn(), complete: vi.fn(finish), finish };
      controls.push(playback);
      return Object.assign(promise, playback);
    }),
  };
});
vi.mock('motion-v', async () => {
  const { ref } = await import('vue');
  return { useAnimate: () => [ref(), animate] };
});
let motionChanged: (event: { matches: boolean }) => void;
let reduced = false;
beforeEach(() => {
  controls.length = 0;
  animate.mockClear();
  reduced = false;
  vi.spyOn(window, 'matchMedia').mockImplementation(() => ({
    matches: reduced,
    addEventListener: vi.fn((_event, listener) => { motionChanged = listener as typeof motionChanged; }),
    removeEventListener: vi.fn(),
  }) as unknown as MediaQueryList);
});
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('pauses and resumes one reveal, then leaves a completed portrait still on foreground changes', async () => {
  const view = mount(PortraitMirror, { props: { open: false } });
  try {
    expect(animate).not.toHaveBeenCalled();
    await view.setProps({ open: true });
    expect(view.attributes('data-mirror')).toBe('revealing');
    await view.setProps({ active: false });
    expect(controls[0].pause).toHaveBeenCalledOnce();
    await view.setProps({ active: true });
    expect(animate).toHaveBeenCalledOnce();
    controls[0].finish();
    await flushPromises();
    expect(view.attributes('data-mirror')).toBe('open');
    await view.setProps({ active: false });
    await view.setProps({ active: true });
    expect(animate).toHaveBeenCalledOnce();
  } finally { view.unmount(); }
});

it('resets an interrupted reveal without letting its completion open the new closed state', async () => {
  const view = mount(PortraitMirror, { props: { open: true } });
  try {
    await view.setProps({ open: false });
    await flushPromises();
    expect(controls[0].complete).toHaveBeenCalledOnce();
    expect(view.attributes('data-mirror')).toBe('closed');
    await view.setProps({ open: true });
    expect(animate).toHaveBeenCalledTimes(2);
    expect(view.attributes('data-mirror')).toBe('revealing');
  } finally { view.unmount(); }
});

it('settles immediately when reduced motion is enabled during the reveal and does not replay when disabled', async () => {
  const view = mount(PortraitMirror, { props: { open: true } });
  try {
    motionChanged({ matches: true });
    await flushPromises();
    expect(view.attributes('data-mirror')).toBe('open');
    motionChanged({ matches: false });
    await flushPromises();
    expect(animate).toHaveBeenCalledOnce();
  } finally { view.unmount(); }
});

it.each(['reduced', 'seen'])('shows a static mirror for %s portraits without starting playback', async reason => {
  reduced = reason === 'reduced';
  const view = mount(PortraitMirror, { props: { open: true, animateReveal: reason !== 'seen' } });
  try {
    await flushPromises();
    expect(view.attributes('data-mirror')).toBe('open');
    expect(animate).not.toHaveBeenCalled();
  } finally { view.unmount(); }
});

it('finishes and releases its playback on unmount', () => {
  const view = mount(PortraitMirror, { props: { open: true } });
  view.unmount();
  expect(controls[0].complete).toHaveBeenCalledOnce();
});

it('replays a seen portrait on request and ignores repeated activation while playing', async () => {
  const view = mount(PortraitMirror, { props: { open: true, animateReveal: false } });
  try {
    await flushPromises();
    const button = view.get('.mirror-replay');
    expect(button.attributes('aria-disabled')).toBe('false');
    await button.trigger('click');
    expect(animate).toHaveBeenCalledOnce();
    expect(view.attributes('data-mirror')).toBe('revealing');
    expect(button.attributes('aria-disabled')).toBe('true');
    await button.trigger('click');
    expect(view.emitted('replay')).toHaveLength(1);
    controls[0].finish();
    await flushPromises();
    expect(view.attributes('data-mirror')).toBe('open');
    expect(button.attributes('aria-disabled')).toBe('false');
  } finally { view.unmount(); }
});

it('does not replay offscreen and immediately clears a replay if the portrait becomes unavailable', async () => {
  const view = mount(PortraitMirror, { props: { open: true, animateReveal: false, active: false } });
  try {
    await flushPromises();
    await view.get('.mirror-replay').trigger('click');
    expect(animate).not.toHaveBeenCalled();
    await view.setProps({ active: true });
    await view.get('.mirror-replay').trigger('click');
    expect(animate).toHaveBeenCalledOnce();
    await view.setProps({ replayable: false });
    await flushPromises();
    expect(view.attributes('data-mirror')).toBe('open');
    expect(view.find('.mirror-replay').exists()).toBe(false);
    expect(controls[0].complete).toHaveBeenCalledOnce();
  } finally { view.unmount(); }
});

it('offers no replay for reduced motion or a missing portrait', async () => {
  reduced = true;
  const view = mount(PortraitMirror, { props: { open: true } });
  try {
    await flushPromises();
    expect(view.find('.mirror-replay').exists()).toBe(false);
    await view.setProps({ replayable: false });
    motionChanged({ matches: false });
    await flushPromises();
    expect(view.find('.mirror-replay').exists()).toBe(false);
  } finally { view.unmount(); }
});

it('animates waiting only during preparation, pauses it offscreen and releases the observer when the portrait opens', async () => {
  let intersect!: (entries: Array<{ isIntersecting: boolean }>) => void;
  const disconnect = vi.fn();
  vi.stubGlobal('IntersectionObserver', class {
    constructor(callback: typeof intersect) { intersect = callback; }
    observe() {}
    disconnect = disconnect;
  });
  const view = mount(PortraitMirror, { props: { open: false, preparing: true } });
  try {
    expect(view.find('.mirror-waiting-glow').exists()).toBe(true);
    intersect([{ isIntersecting: false }]);
    await flushPromises();
    expect(view.classes()).toContain('mirror-inactive');
    intersect([{ isIntersecting: true }]);
    await view.setProps({ active: false });
    expect(view.classes()).toContain('mirror-inactive');
    await view.setProps({ active: true });
    expect(view.classes()).not.toContain('mirror-inactive');
    await view.setProps({ open: true });
    expect(view.find('.mirror-waiting-glow').exists()).toBe(false);
    expect(disconnect).toHaveBeenCalled();
  } finally { view.unmount(); }
});
