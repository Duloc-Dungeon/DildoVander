import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import WnsNavigation from './WnsNavigation.vue';

describe('WNS customer navigation', () => {
  it('links the four approved destinations without hardware or staff tools', () => {
    const wrapper = mount(WnsNavigation, { global: { stubs: { RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' } } } });
    expect(wrapper.findAll('a').map(link => [link.text(), link.attributes('href')])).toEqual([
      ['Večer', '/play'], ['Postava', '/me/character'], ['Památky', '/me/keepsakes'], ['Já', '/me/account'],
    ]);
    expect(wrapper.text()).not.toContain('kufr');
  });
});
