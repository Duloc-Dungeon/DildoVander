import { mount } from '@vue/test-utils';
import { expect, it } from 'vitest';
import CharacterIntroduction from './CharacterIntroduction.vue';

it('keeps unchanged copy still on foreground updates and reintroduces it only for an explicit replay', async () => {
  const view = mount(CharacterIntroduction, { props: { text: 'Stejná osobní věta.', active: true } });
  try {
    const initial = view.get('p').element;
    await view.setProps({ active: false });
    expect(view.get('p').element).toBe(initial);
    expect(view.get('p').classes()).toContain('character-introduction-paused');
    await view.setProps({ active: true });
    expect(view.get('p').element).toBe(initial);
    await view.setProps({ replayKey: 1 });
    expect(view.get('p').element).not.toBe(initial);
    expect(view.text()).toBe('Stejná osobní věta.');
  } finally { view.unmount(); }
});
