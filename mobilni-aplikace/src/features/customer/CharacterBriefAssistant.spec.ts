import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import CharacterBriefAssistant from './CharacterBriefAssistant.vue';

describe('character brief assistance', () => {
  it('keeps the existing description and adds only an explicitly selected idea', async () => {
    const wrapper = mount(CharacterBriefAssistant, { props: { modelValue: 'Moje vlastní postava.' } });
    expect(wrapper.emitted('update:modelValue')).toBeUndefined();
    await wrapper.find('button').trigger('click');
    const value = wrapper.emitted('update:modelValue')![0][0] as string;
    expect(value).toMatch(/^Moje vlastní postava\.\nNavenek/);
    await wrapper.setProps({ modelValue: value });
    expect(wrapper.find('button').attributes('disabled')).toBeDefined();
    expect(wrapper.get('[role="status"]').text()).toContain('Nápad je v popisu');
  });

  it('does not overwrite a full description or allow changes while saving', async () => {
    const wrapper = mount(CharacterBriefAssistant, { props: { modelValue: 'a'.repeat(1199) } });
    expect(wrapper.findAll('button').every(button => button.attributes('disabled') !== undefined)).toBe(true);
    await wrapper.setProps({ modelValue: '', disabled: true });
    await wrapper.find('button').trigger('click');
    expect(wrapper.emitted('update:modelValue')).toBeUndefined();
  });
});
