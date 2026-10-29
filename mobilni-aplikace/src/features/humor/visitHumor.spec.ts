import { afterEach, expect, it, vi } from 'vitest';
import { defineComponent, nextTick, ref } from 'vue';
import { mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import { humorVariants, useVisitHumor } from './visitHumor';
import { characterIntroductionText } from '../launch/characterWelcomeText';

afterEach(() => vi.restoreAllMocks());
it('selects on route entry and keeps copy stable through polling and query changes', async () => {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:pathMatch(.*)*', component: { template: '<div />' } }] });
  await router.push('/one');
  const random = vi.spyOn(Math, 'random').mockReturnValue(0);
  const poll = ref(0);
  const view = mount(defineComponent({ setup() { return { ...useVisitHumor(), poll }; }, template: '<p>{{ copy.portrait }} {{ poll }}</p>' }), { global: { plugins: [router] } });
  expect(view.text()).toContain(humorVariants.portrait[0]);
  random.mockReturnValue(.99);
  poll.value++; await nextTick();
  await router.push('/one?refresh=1');
  expect(view.text()).toContain(humorVariants.portrait[0]);
  await router.push('/two');
  expect(view.text()).toContain(humorVariants.portrait[2]);
  random.mockReturnValue(.5);
  await router.push('/one');
  expect(view.text()).toContain(humorVariants.portrait[1]);
  view.unmount();
});
it('uses stored character variants without changing them on a fresh polling object', () => {
  const artwork = { status: 'ready', welcome: { joke: 'První.', jokes: ['První.', 'Druhý.', 'Třetí.'], identity: '', teaser: '' } };
  expect(characterIntroductionText(artwork, true, .5)).toBe('Druhý.');
  expect(characterIntroductionText(structuredClone(artwork), true, .5)).toBe('Druhý.');
  expect(characterIntroductionText({ status: 'ready', welcome: { ...artwork.welcome, jokes: undefined } }, true, .5)).toBe('První.');
  expect(characterIntroductionText({ status: 'ready', welcome: { ...artwork.welcome, jokes: ['null', 'a', 'b'] } }, true, .5)).toBe('První.');
});
it('has three distinct finished alternatives in each static pool', () => {
  for (const variants of Object.values(humorVariants)) {
    expect(new Set(variants).size).toBe(3);
    expect(variants.every(text => text.length > 10 && text.length < 150)).toBe(true);
  }
});
