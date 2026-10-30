import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import WandRevealField from './WandRevealField.vue';
const timeline = vi.hoisted(() => ({ clock: 0, animate: vi.fn(), cancel: vi.fn() }));
vi.mock('motion-v', async () => {
  const { ref } = await import('vue');
  return { useAnimate: () => [ref(), (...args: unknown[]) => {
    timeline.animate(...args);
    return { get time() { return timeline.clock / 1000; }, cancel: timeline.cancel, then() {} };
  }] };
});

describe('finite wand reveal field', () => {
  let view: VueWrapper;
  let motion: { matches: boolean; addEventListener: ReturnType<typeof vi.fn>; removeEventListener: ReturnType<typeof vi.fn> };
  let preferenceChanged: () => void;
  let intersect: (entries: { isIntersecting: boolean }[]) => void;
  let nextFrame: FrameRequestCallback | undefined;
  let clock = 0;
  const paint = {
    clearRect: vi.fn(), fillRect: vi.fn(), setTransform: vi.fn(), save: vi.fn(), restore: vi.fn(),
    translate: vi.fn(), rotate: vi.fn(), beginPath: vi.fn(), moveTo: vi.fn(),
    lineTo: vi.fn(), stroke: vi.fn(), ellipse: vi.fn(), fill: vi.fn(),
    bezierCurveTo: vi.fn(),
    createRadialGradient: vi.fn(() => ({ addColorStop: vi.fn() })),
    createLinearGradient: vi.fn(() => ({ addColorStop: vi.fn() })),
  };
  beforeEach(() => {
    clock = 0;
    timeline.clock = 0;
    nextFrame = undefined;
    vi.spyOn(performance, 'now').mockImplementation(() => clock);
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(paint as unknown as CanvasRenderingContext2D);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({
      width: 224, height: 224, top: 0, left: 0, bottom: 224, right: 224, x: 0, y: 0, toJSON: () => ({}),
    });
    motion = {
      matches: false,
      addEventListener: vi.fn((_event, callback) => { preferenceChanged = callback; }),
      removeEventListener: vi.fn(),
    };
    vi.stubGlobal('matchMedia', (query: string) => query.includes('min-width')
      ? { matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() } : motion);
    vi.stubGlobal('requestAnimationFrame', vi.fn(callback => { nextFrame = callback; return 1; }));
    vi.stubGlobal('cancelAnimationFrame', vi.fn(() => { nextFrame = undefined; }));
    vi.stubGlobal('IntersectionObserver', class {
      constructor(callback: typeof intersect) { intersect = callback; }
      observe() {}
      disconnect() {}
    });
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  });
  afterEach(() => {
    view?.unmount();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });
  async function advance(milliseconds: number) {
    clock = milliseconds;
    timeline.clock = milliseconds;
    const callback = nextFrame;
    nextFrame = undefined;
    callback?.(clock);
    await view.vm.$nextTick();
  }
  it('draws a finite field, caps pixel density and leaves the image untouched', async () => {
    vi.stubGlobal('devicePixelRatio', 3);
    view = mount(WandRevealField);
    expect(view.attributes('aria-hidden')).toBe('true');
    expect(document.body.querySelector('canvas.wand-reveal-field')?.getAttribute('width')).toBe('336');
    await advance(16);
    expect(paint.stroke).toHaveBeenCalled();
    expect(document.body.querySelector('.wand-reveal-backdrop')).not.toBeNull();
    expect(timeline.animate).toHaveBeenCalledOnce();
    await advance(4201);
    expect(view.attributes('data-state')).toBe('settled');
    expect(nextFrame).toBeUndefined();
  });
  it('teleports the desktop field outside clipping ancestors and sizes it to the viewport', async () => {
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      const viewport = this.classList.contains('wand-reveal-viewport');
      const width = viewport ? 1004 : 224;
      const height = viewport ? 804 : 224;
      const left = viewport ? -2 : 0;
      const top = viewport ? -2 : 0;
      return { width, height, left, top, right: left + width, bottom: top + height, x: left, y: top, toJSON: () => ({}) };
    });
    vi.stubGlobal('matchMedia', (query: string) => query.includes('min-width')
      ? { matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn() } : motion);
    view = mount(WandRevealField);
    await view.vm.$nextTick();
    const surface = document.body.querySelector('canvas.wand-reveal-field');
    expect(surface?.closest('.wand-reveal-viewport')?.parentElement).toBe(document.body);
    expect(surface?.getAttribute('width')).toBe(String(Math.round(1004 * Math.min(devicePixelRatio || 1, 1.5))));
    expect(surface?.getAttribute('height')).toBe(String(Math.round(804 * Math.min(devicePixelRatio || 1, 1.5))));
    window.dispatchEvent(new Event('scroll'));
    await advance(2900);
    expect(view.attributes('data-state')).toBe('settled');
    expect(nextFrame).toBeUndefined();
    view.unmount();
    expect(document.body.querySelector('canvas.wand-reveal-field')).toBeNull();
  });
  it('does not schedule frames for reduced motion, including touch', async () => {
    motion.matches = true;
    view = mount(WandRevealField);
    window.dispatchEvent(new PointerEvent('pointerdown', { clientX: 80, clientY: 90, pointerType: 'touch' }));
    await view.vm.$nextTick();
    expect(view.attributes('data-state')).toBe('disabled');
    expect(requestAnimationFrame).not.toHaveBeenCalled();
  });
  it('stops immediately when the motion preference changes', async () => {
    view = mount(WandRevealField);
    motion.matches = true;
    preferenceChanged();
    await view.vm.$nextTick();
    expect(view.attributes('data-state')).toBe('disabled');
    expect(nextFrame).toBeUndefined();
  });
  it('keeps the fullscreen reveal when mobile layout moves its landing offscreen', async () => {
    view = mount(WandRevealField);
    intersect([{ isIntersecting: false }]);
    await view.vm.$nextTick();
    expect(nextFrame).toBeDefined();
    await advance(2900);
    expect(view.attributes('data-state')).toBe('settled');
    intersect([{ isIntersecting: true }]);
    expect(nextFrame).toBeUndefined();
  });
  it('bends the active field with passive touch but never replays for a returning pointer', async () => {
    view = mount(WandRevealField);
    await advance(16);
    const originalTranslation = paint.translate.mock.calls.at(-1);
    const event = new PointerEvent('pointerdown', { clientX: 80, clientY: 90, pointerType: 'touch', cancelable: true });
    window.dispatchEvent(event);
    await advance(32);
    expect(event.defaultPrevented).toBe(false);
    expect(view.attributes('data-state')).toBe('active');
    expect(paint.translate.mock.calls.at(-1)).not.toEqual(originalTranslation);
    await advance(4201);
    expect(view.attributes('data-state')).toBe('settled');
    window.dispatchEvent(new PointerEvent('pointerdown', { clientX: 80, clientY: 90, pointerType: 'touch' }));
    window.dispatchEvent(new PointerEvent('pointermove', { clientX: 190, clientY: 90 }));
    await advance(5000);
    expect(view.attributes('data-state')).toBe('settled');
    expect(nextFrame).toBeUndefined();
  });
  it('stops in a hidden document and does not replay on return', async () => {
    const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    view = mount(WandRevealField);
    hidden.mockReturnValue(true);
    document.dispatchEvent(new Event('visibilitychange'));
    await view.vm.$nextTick();
    expect(nextFrame).toBeUndefined();
    hidden.mockReturnValue(false);
    document.dispatchEvent(new Event('visibilitychange'));
    expect(nextFrame).toBeUndefined();
  });
  it('falls back to the static artwork if rendering is consistently too slow', async () => {
    view = mount(WandRevealField);
    for (let time = 150; time <= 1800; time += 150) await advance(time);
    expect(view.attributes('data-state')).toBe('disabled');
    expect(nextFrame).toBeUndefined();
  });
  it('does not lose the mobile reveal when the keyboard closes or the page scrolls', async () => {
    view = mount(WandRevealField);
    window.dispatchEvent(new Event('resize'));
    window.dispatchEvent(new Event('scroll'));
    await advance(1000);
    expect(view.attributes('data-state')).toBe('active');
    await advance(2900);
    expect(view.attributes('data-state')).toBe('settled');
  });
  it('keeps a missing Canvas implementation harmless', async () => {
    vi.mocked(HTMLCanvasElement.prototype.getContext).mockReturnValue(null);
    view = mount(WandRevealField);
    await view.vm.$nextTick();
    expect(view.attributes('data-state')).toBe('disabled');
    expect(requestAnimationFrame).not.toHaveBeenCalled();
  });
  it('restores the static image if the motion driver cannot start', async () => {
    timeline.animate.mockImplementationOnce(() => { throw new Error('Unsupported animation backend'); });
    view = mount(WandRevealField);
    await view.vm.$nextTick();
    expect(view.attributes('data-state')).toBe('disabled');
    expect(document.body.querySelector('.wand-reveal-viewport')?.getAttribute('style')).toContain('display: none');
    expect(nextFrame).toBeUndefined();
  });
  it('cancels the pending frame and removes listeners on unmount', () => {
    view = mount(WandRevealField);
    view.unmount();
    expect(nextFrame).toBeUndefined();
    expect(timeline.cancel).toHaveBeenCalled();
    expect(motion.removeEventListener).toHaveBeenCalledWith('change', preferenceChanged);
  });
});
