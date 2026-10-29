import { mount } from '@vue/test-utils';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { nextTick } from 'vue';
import ViewportCurtain from '../../components/scene/ViewportCurtain.vue';

const motion = vi.hoisted(() => ({ animate: vi.fn(), stop: vi.fn() }));
vi.mock('motion-v', async () => {
  const { ref: createRef } = await import('vue');
  return { useAnimate: () => [createRef(null), motion.animate] };
});

let finishAnimation: (() => void) | undefined;

beforeEach(() => {
  vi.useFakeTimers();
  motion.stop.mockReset();
  motion.animate.mockReset().mockImplementation(() => ({
    stop: motion.stop,
    then: (callback: () => void) => { finishAnimation = callback; }
  }));
  finishAnimation = undefined;
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => setTimeout(() => callback(performance.now()), 16));
  vi.stubGlobal('cancelAnimationFrame', (handle: number) => clearTimeout(handle));
});
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });

it('removes the opaque cover if Motion cannot initialize', async () => {
  motion.animate.mockImplementationOnce(() => { throw new Error('Animation backend unavailable'); });
  const view = mount(ViewportCurtain, { props: { durationMs: 1100 } });
  try {
    await vi.advanceTimersByTimeAsync(32);
    expect(document.body.querySelector('.viewport-curtain')).toBeNull();
    expect(view.emitted('finished')).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(2000);
    expect(view.emitted('finished')).toHaveLength(1);
  } finally { view.unmount(); }
});

it('commits a closed opaque frame before opening both wings in parallel for the bounded duration', async () => {
  const view = mount(ViewportCurtain, { props: { durationMs: 1_100 } });
  try {
    const root = document.body.querySelector('.viewport-curtain');
    expect(root?.getAttribute('aria-hidden')).toBe('true');
    expect(root?.querySelectorAll('.viewport-curtain__wing')).toHaveLength(2);
    expect(root?.classList.contains('viewport-curtain--opening')).toBe(false);
    expect(motion.animate).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(32);
    expect(motion.animate).toHaveBeenCalledOnce();
    expect(view.emitted('finished')).toBeUndefined();
    const sequence = motion.animate.mock.calls[0][0];
    expect(sequence).toEqual([
      ['.viewport-curtain__wing--left', { x: ['0%', '-105%'] }, { duration: 1.1, ease: [0.22, 1, 0.36, 1], at: 0 }],
      ['.viewport-curtain__wing--right', { x: ['0%', '105%'] }, { duration: 1.1, ease: [0.22, 1, 0.36, 1], at: 0 }]
    ]);
    finishAnimation?.();
    await nextTick();
    expect(document.body.querySelector('.viewport-curtain')).toBeNull();
    expect(view.emitted('finished')).toHaveLength(1);
  } finally { view.unmount(); }
});

it('stops the active Motion sequence on cancellation and emits completion only once', async () => {
  const view = mount(ViewportCurtain, { props: { durationMs: 1_200 } });
  try {
    await vi.advanceTimersByTimeAsync(32);
    expect(motion.animate).toHaveBeenCalledOnce();
    await view.setProps({ active: false });
    expect(document.body.querySelector('.viewport-curtain')).toBeNull();
    expect(motion.stop).toHaveBeenCalledOnce();
    expect(view.emitted('finished')).toHaveLength(1);
    finishAnimation?.();
    expect(view.emitted('finished')).toHaveLength(1);
  } finally { view.unmount(); }
});
