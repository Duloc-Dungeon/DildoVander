import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import CharacterEntrance from './CharacterEntrance.vue';

const character = { profileId: 'synthetic', revision: 1, name: 'Raven', universe: 'Fiction', status: 'ready' };
const welcome = { joke: 'PERSONAL JOKE', identity: 'CHARACTER IDENTITY', teaser: 'NEXT SCENE' };
const initial = { character: null, artwork: null, image: '', failed: false, readFailed: false, active: true, busy: false };
beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe('progressive character entrance', () => {
  it('keeps the general introduction when generated text contains a serialized null', async () => {
    const view = mount(CharacterEntrance, { props: { ...initial, character, artwork: { status: 'running', loadingMessage: 'null', welcome: { ...welcome, joke: ' NULL ' } } } });
    try {
      await vi.advanceTimersByTimeAsync(1000);
      expect(view.attributes('data-entrance')).toBe('waiting');
      expect(view.get('.entrance-joke').text()).toContain('Zrcadlo si zatím vyjednává');
    } finally { view.unmount(); }
  });
  it('shows and clears the display label without changing the character name', async () => {
    const view = mount(CharacterEntrance, { props: { ...initial, character: { ...character, label: 'Petr' }, failed: true } });
    try {
      expect(view.get('h1').text()).toBe('Raven · Petr');
      await view.setProps({ character: { ...character, label: null } });
      expect(view.get('h1').text()).toBe('Raven');
      expect(character.name).toBe('Raven');
    } finally { view.unmount(); }
  });
  it('waits up to five visible minutes while the character and portrait are still loading', async () => {
    const view = mount(CharacterEntrance, { props: initial });
    try {
      expect(view.attributes('data-entrance')).toBe('waiting');
      expect(view.text()).toContain('Zrcadlo si zatím vyjednává');
      expect(view.find('.entrance-continue').exists()).toBe(false);
      await vi.advanceTimersByTimeAsync(299900);
      expect(view.find('.entrance-continue').exists()).toBe(false);
      await vi.advanceTimersByTimeAsync(100);
      await view.get('.entrance-continue').trigger('click');
      expect(view.emitted('continue')).toHaveLength(1);
    } finally { view.unmount(); }
  });
  it('introduces personal copy before revealing an already decoded portrait', async () => {
    const view = mount(CharacterEntrance, { props: initial });
    try {
      await view.setProps({ character, artwork: { status: 'running', loadingMessage: welcome.joke } });
      await vi.advanceTimersByTimeAsync(900);
      expect(view.attributes('data-entrance')).toBe('personal');
      expect(view.text()).toContain(welcome.joke);
      await view.setProps({ image: 'blob:decoded', artwork: { status: 'ready', welcome } });
      expect(view.attributes('data-entrance')).toBe('personal');
      await vi.advanceTimersByTimeAsync(1800);
      expect(view.attributes('data-entrance')).toBe('portrait');
      expect(view.get('h1').text()).toBe('Raven');
      expect(view.get('.entrance-portrait').attributes('aria-hidden')).toBe('false');
      expect(view.text()).toContain(welcome.identity);
      expect(view.find('.entrance-continue').exists()).toBe(true);
    } finally { view.unmount(); }
  });
  it.each(['failed', 'read-failed', 'not-requested'])('offers one manual retry then continuation without a delay after %s', async cause => {
    const view = mount(CharacterEntrance, { props: { ...initial, character } });
    try {
      if (cause === 'failed') await view.setProps({ failed: true });
      if (cause === 'read-failed') await view.setProps({ readFailed: true });
      if (cause === 'not-requested') await view.setProps({ artwork: { status: 'not_requested' } });
      expect(view.attributes('data-entrance')).toBe('placeholder');
      expect(view.get('h1').text()).toBe('Raven');
      expect(view.text()).toContain('Malíř prý zachytil tvou podstatu');
      expect(view.find('.entrance-continue').exists()).toBe(false);
      await view.get('.entrance-retry').trigger('click');
      expect(view.emitted('retry')).toHaveLength(1);
      await view.setProps({ retryAttempted: true, retrying: true });
      expect(view.find('.entrance-continue').exists()).toBe(false);
      expect(view.find('.entrance-retry').exists()).toBe(false);
      await view.setProps({ retrying: false });
      await view.get('.entrance-continue').trigger('click');
      expect(view.emitted('continue')).toHaveLength(1);
    } finally { view.unmount(); }
  });
  it('can replace the placeholder with a late portrait and pauses the presentation in the background', async () => {
    const view = mount(CharacterEntrance, { props: initial });
    try {
      await view.setProps({ active: false });
      await vi.advanceTimersByTimeAsync(20000);
      expect(view.attributes('data-entrance')).toBe('waiting');
      expect(view.find('.entrance-continue').exists()).toBe(false);
      await view.setProps({ active: true });
      await vi.advanceTimersByTimeAsync(300000);
      expect(view.attributes('data-entrance')).toBe('placeholder');
      await view.setProps({ character, image: 'blob:late', artwork: { status: 'ready', welcome } });
      await vi.advanceTimersByTimeAsync(2700);
      expect(view.attributes('data-entrance')).toBe('portrait');
    } finally { view.unmount(); }
  });
  it('caps a never-settling preparation retry at five minutes while keeping entry verification busy separate', async () => {
    const view = mount(CharacterEntrance, { props: { ...initial, retryAttempted: true, retrying: true } });
    try {
      await vi.advanceTimersByTimeAsync(299900);
      expect(view.find('.entrance-continue').exists()).toBe(false);
      await vi.advanceTimersByTimeAsync(100);
      expect(view.get('.entrance-continue').attributes('disabled')).toBeUndefined();
      await view.setProps({ busy: true });
      expect(view.find('.entrance-continue').exists()).toBe(false);
    } finally { view.unmount(); }
  });
});
