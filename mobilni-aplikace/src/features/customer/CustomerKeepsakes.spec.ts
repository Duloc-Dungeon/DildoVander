import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import CustomerKeepsakes from './CustomerKeepsakes.vue';

describe('CustomerKeepsakes', () => {
  it('keeps the results collection primary and mounts secondary sections only when opened', async () => {
    const wrapper = mount(CustomerKeepsakes, {
      props: { locale: 'cs' },
      global: {
        stubs: {
          AccountArchiveList: { template: '<section data-testid="collection" />' },
          CustomerCompletionUpdates: { template: '<section data-testid="updates" />' },
          CustomerGameTitles: { template: '<section data-testid="titles" />' },
          CustomerSealedCaseRewards: { template: '<section data-testid="cases" />' },
          CustomerDuelHistory: { template: '<section data-testid="battles" />' },
        },
      },
    });

    expect(wrapper.find('[data-testid="collection"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="updates"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="titles"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="cases"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="battles"]').exists()).toBe(false);

    const details = wrapper.findAll('details');
    (details[1].element as HTMLDetailsElement).open = true;
    await details[1].trigger('toggle');
    expect(wrapper.find('[data-testid="titles"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="updates"]').exists()).toBe(false);
  });
});
