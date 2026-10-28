import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import CharacterEntrance from './CharacterEntrance.vue';

const character = { profileId: 'synthetic', revision: 1, name: 'Raven', universe: 'Fiction', status: 'ready' };
const welcome = { joke: 'PERSONAL JOKE', identity: 'CHARACTER IDENTITY', teaser: 'NEXT SCENE' };
const initial = { character: null, artwork: null, image: '', failed: false, readFailed: false, active: true, busy: false };
beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe('progressive character entrance', () => {
  it('shows and clears the display label without changing the character name', async () => {
    const view = mount(CharacterEntrance, { props: { ...initial, character: { ...character, label: 'Petr' }, failed: true } });
    try {
      expect(view.get('h1').text()).toBe('Raven · Petr');
      await view.setProps({ character: { ...character, label: null } });
      expect(view.get('h1').text()).toBe('Raven');
      expect(character.name).toBe('Raven');
    } finally { view.unmount(); }
  });
  it('opens with a stable general joke and only offers continuation after ten visible seconds', async () => {
    const view = mount(CharacterEntrance, { props: initial });
    try {
      expect(view.attributes('data-entrance')).toBe('waiting');
      expect(view.text()).toContain('Slavná osoba se ještě hledá');
      expect(view.find('.entrance-continue').exists()).toBe(false);
      await vi.advanceTimersByTimeAsync(9900);
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
      expect(view.find('.entrance-continue').exists()).toBe(false);
    } finally { view.unmount(); }
  });
  it.each(['failed', 'read-failed', 'not-requested', 'timeout'])('offers a placeholder and continuation after %s', async cause => {
    const view = mount(CharacterEntrance, { props: { ...initial, character } });
    try {
      if (cause === 'failed') await view.setProps({ failed: true });
      if (cause === 'read-failed') await view.setProps({ readFailed: true });
      if (cause === 'not-requested') await view.setProps({ artwork: { status: 'not_requested' } });
      if (cause === 'timeout') await vi.advanceTimersByTimeAsync(15000);
      expect(view.attributes('data-entrance')).toBe('placeholder');
      expect(view.get('h1').text()).toBe('Raven');
      expect(view.text()).toContain('Malíř prý zachytil tvou podstatu');
      if (cause !== 'timeout') await vi.advanceTimersByTimeAsync(10000);
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
      await vi.advanceTimersByTimeAsync(15000);
      expect(view.attributes('data-entrance')).toBe('placeholder');
      await view.setProps({ character, image: 'blob:late', artwork: { status: 'ready', welcome } });
      await vi.advanceTimersByTimeAsync(2700);
      expect(view.attributes('data-entrance')).toBe('portrait');
    } finally { view.unmount(); }
  });
});
