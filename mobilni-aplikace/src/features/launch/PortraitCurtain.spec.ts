import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import PortraitCurtain from './PortraitCurtain.vue';
import { presentationForeground } from './launchPresentation';
const motion = vi.hoisted(() => ({ animate: vi.fn(), stop: vi.fn() }));
vi.mock('motion-v', async () => {
  const { ref } = await import('vue');
  return { useAnimate: () => [ref(null), motion.animate] };
});
let intersect: (entries: any[]) => void;
let reduced = false;
function visibility(ratio: number) { intersect([{ isIntersecting: ratio > 0, intersectionRatio: ratio }]); }
function stage(identity = '["guest","profile",1]') { return mount(PortraitCurtain, { props: { image: 'blob:already-authorised-and-decoded', identity, alt: 'Portrét tvé postavy', preparing: false } }); }
beforeEach(() => {
  vi.useFakeTimers(); localStorage.clear(); reduced = false; presentationForeground.value = true;
  motion.stop.mockReset();
  motion.animate.mockReset().mockImplementation((sequence: any[]) => {
    let completion: ReturnType<typeof setTimeout> | undefined;
    return {
      stop: () => { motion.stop(); if (completion) clearTimeout(completion); },
      then: (callback: () => void) => {
        completion = setTimeout(callback, sequence[0][2].duration * 1_000);
      }
    };
  });
  vi.stubGlobal('IntersectionObserver', class { constructor(callback: any) { intersect = callback; } observe() {} disconnect() {} });
  vi.stubGlobal('matchMedia', () => ({ matches: reduced, addEventListener() {}, removeEventListener() {} }));
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => setTimeout(() => callback(performance.now()), 16));
  vi.stubGlobal('cancelAnimationFrame', (handle: number) => clearTimeout(handle));
});
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });
it('F: interrupted opening stays unseen and replays fully above the same image', async () => {
  const view = stage();
  try {
    visibility(.49); await nextTick(); expect(view.attributes('data-curtain')).toBe('closed');
    visibility(.5); await nextTick(); expect(view.attributes('data-curtain')).toBe('opening');
    await vi.advanceTimersByTimeAsync(600); visibility(.2); await nextTick();
    expect(view.attributes('data-curtain')).toBe('closed'); expect(localStorage.length).toBe(0);
    visibility(.8); await nextTick(); await vi.advanceTimersByTimeAsync(1131);
    expect(localStorage.length).toBe(0); await vi.advanceTimersByTimeAsync(1);
    expect(view.attributes('data-curtain')).toBe('open'); expect(localStorage.length).toBe(1);
    expect(document.body.querySelector('.viewport-curtain')).toBeNull();
    expect(view.get('.portrait-stage-image').attributes('src')).toBe('blob:already-authorised-and-decoded');
  } finally { view.unmount(); }
});
it('G: a seen revision returns open and a new revision starts closed', async () => {
  let view = stage(); visibility(1); await vi.advanceTimersByTimeAsync(1132); view.unmount();
  view = stage(); await nextTick(); expect(view.attributes('data-curtain')).toBe('open'); view.unmount();
  view = stage('["guest","profile",2]'); await nextTick(); expect(view.attributes('data-curtain')).toBe('closed'); view.unmount();
});
it('teleports the opening curtain over the viewport and removes it when the run is cancelled', async () => {
  const view = stage();
  try {
    visibility(1); await nextTick();
    const curtain = document.body.querySelector('.viewport-curtain');
    expect(curtain?.getAttribute('aria-hidden')).toBe('true');
    expect(curtain?.classList.contains('viewport-curtain--opening')).toBe(false);
    expect(curtain?.querySelectorAll('.viewport-curtain__wing')).toHaveLength(2);
    visibility(.2); await nextTick();
    expect(document.body.querySelector('.viewport-curtain')).toBeNull();
  } finally { view.unmount(); }
});
it('backgrounding and account switches cannot mark an incomplete frame seen', async () => {
  const view = stage();
  try {
    visibility(1); await vi.advanceTimersByTimeAsync(700); presentationForeground.value = false; await nextTick();
    await vi.advanceTimersByTimeAsync(3000); expect(localStorage.length).toBe(0); expect(view.attributes('data-curtain')).toBe('closed');
    presentationForeground.value = true; await nextTick(); await vi.advanceTimersByTimeAsync(500);
    await view.setProps({ image: '', identity: '["other","another",1]' });
    await vi.advanceTimersByTimeAsync(2000); expect(localStorage.length).toBe(0); expect(view.find('.portrait-stage-image').exists()).toBe(false);
  } finally { view.unmount(); }
});
it('L: reduced motion records a static display after paint without animationend', async () => {
  reduced = true; const view = stage();
  try {
    visibility(1); await nextTick(); expect(view.attributes('data-curtain')).toBe('open'); expect(localStorage.length).toBe(0);
    await vi.advanceTimersByTimeAsync(32); expect(localStorage.length).toBe(1); expect(view.emitted('revealed')?.at(-1)).toEqual([true]);
  } finally { view.unmount(); }
});
it('shows the missing portrait without marking an image as revealed or seen', async () => {
  const view = stage();
  try {
    await view.setProps({ image: '', unavailable: true });
    visibility(1); await vi.advanceTimersByTimeAsync(2000);
    expect(view.classes()).toContain('is-open');
    expect(view.text()).toContain('Portrét zatím chybí.');
    expect(localStorage.length).toBe(0);
    expect(view.emitted('revealed')?.some(event => event[0] === true)).toBe(false);
  } finally { view.unmount(); }
});
