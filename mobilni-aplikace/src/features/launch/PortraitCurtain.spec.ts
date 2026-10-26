import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import PortraitCurtain from './PortraitCurtain.vue';
import { presentationForeground } from './launchPresentation';
let intersect: (entries: any[]) => void;
let reduced = false;
function visibility(ratio: number) { intersect([{ isIntersecting: ratio > 0, intersectionRatio: ratio }]); }
function stage(identity = '["guest","profile",1]') { return mount(PortraitCurtain, { props: { image: 'blob:already-authorised-and-decoded', identity, alt: 'Portrét tvé postavy', preparing: false } }); }
beforeEach(() => {
  vi.useFakeTimers(); localStorage.clear(); reduced = false; presentationForeground.value = true;
  vi.stubGlobal('IntersectionObserver', class { constructor(callback: any) { intersect = callback; } observe() {} disconnect() {} });
  vi.stubGlobal('matchMedia', () => ({ matches: reduced, addEventListener() {}, removeEventListener() {} }));
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => setTimeout(() => callback(performance.now()), 16));
});
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });
it('F: interrupted opening stays unseen and replays fully above the same image', async () => {
  const view = stage();
  try {
    visibility(.49); await nextTick(); expect(view.attributes('data-curtain')).toBe('closed');
    visibility(.5); await nextTick(); expect(view.attributes('data-curtain')).toBe('opening');
    await vi.advanceTimersByTimeAsync(600); visibility(.2); await nextTick();
    expect(view.attributes('data-curtain')).toBe('closed'); expect(localStorage.length).toBe(0);
    visibility(.8); await nextTick(); await vi.advanceTimersByTimeAsync(1099);
    expect(localStorage.length).toBe(0); await vi.advanceTimersByTimeAsync(1);
    expect(view.attributes('data-curtain')).toBe('open'); expect(localStorage.length).toBe(1);
    expect(view.get('.portrait-stage-image').attributes('src')).toBe('blob:already-authorised-and-decoded');
  } finally { view.unmount(); }
});
it('G: a seen revision returns open and a new revision starts closed', async () => {
  let view = stage(); visibility(1); await vi.advanceTimersByTimeAsync(1100); view.unmount();
  view = stage(); await nextTick(); expect(view.attributes('data-curtain')).toBe('open'); view.unmount();
  view = stage('["guest","profile",2]'); await nextTick(); expect(view.attributes('data-curtain')).toBe('closed'); view.unmount();
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
