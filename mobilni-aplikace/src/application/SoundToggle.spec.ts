import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import SoundToggle from './SoundToggle.vue';
import { wnsSound } from './wnsSound';

beforeEach(() => wnsSound.enable(false));

describe('sound toggle', () => {
  it('shows the current state accessibly and toggles sound', async () => {
    const wrapper = mount(SoundToggle);
    const button = wrapper.get('button');
    expect(button.attributes('aria-pressed')).toBe('false');
    expect(button.attributes('aria-label')).toBe('Zvuk vypnutý, zapnout');
    expect(button.text()).toContain('Vypnuto');

    await button.trigger('click');
    expect(button.attributes('aria-pressed')).toBe('true');
    expect(button.attributes('aria-label')).toBe('Zvuk zapnutý, vypnout');
    expect(button.text()).toContain('Zapnuto');
    wrapper.unmount();
  });
});
