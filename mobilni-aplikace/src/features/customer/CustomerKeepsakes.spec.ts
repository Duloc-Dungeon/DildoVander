import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import CustomerKeepsakes from './CustomerKeepsakes.vue';

describe('CustomerKeepsakes', () => {
  it('keeps the illustrated chronicle primary and mounts older records only when opened', async () => {
    const wrapper = mount(CustomerKeepsakes, {
      props: { locale: 'cs' },
      global: {
        stubs: {
          CustomerChronicle: { template: '<section data-testid="chronicle" />' },
          AccountArchiveList: { template: '<section data-testid="collection" />' },
          CustomerCompletionUpdates: { template: '<section data-testid="updates" />' },
          CustomerGameTitles: { template: '<section data-testid="titles" />' },
          CustomerSealedCaseRewards: { template: '<section data-testid="cases" />' },
          CustomerDuelHistory: { template: '<section data-testid="battles" />' },
        },
      },
    });

    expect(wrapper.find('[data-testid="chronicle"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="collection"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="updates"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="titles"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="cases"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="battles"]').exists()).toBe(false);

    const details = wrapper.findAll('details');
    (details[0].element as HTMLDetailsElement).open = true;
    await details[0].trigger('toggle');
    expect(wrapper.find('[data-testid="collection"]').exists()).toBe(true);
    const disclosures = wrapper.findAll('.customer-keepsakes__disclosure');
    (disclosures[1].element as HTMLDetailsElement).open = true;
    await disclosures[1].trigger('toggle');
    expect(wrapper.find('[data-testid="titles"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="updates"]').exists()).toBe(false);
  });
});
