import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import TrunkStage from './TrunkStage.vue';

describe('workshop presentation boundaries', () => {
  it('reacts only to real speaking output and clamps invalid or stale measurements', async () => {
    const view = mount(TrunkStage, { props: { stage: 'speaking', assistantOutputLevel: .75, activityLabel: 'Kufr má slovo' } });
    const level = () => view.get('.trunk-stage__visual').attributes('style');
    expect(level()).toContain('--trunk-level: 0.75');
    await view.setProps({ assistantOutputLevel: 5 });
    expect(level()).toContain('--trunk-level: 1;');
    await view.setProps({ assistantOutputLevel: Number.NaN });
    expect(level()).toContain('--trunk-level: 0;');
    await view.setProps({ stage: 'waiting', assistantOutputLevel: .9 });
    expect(level()).toContain('--trunk-level: 0;');
    view.unmount();
  });
  it('never invents an illustration when manufacturing completes without artwork', () => {
    const view = mount(TrunkStage, { props: { stage: 'revealed', artworkStatus: 'running', wandName: 'Uložená hůlka' } });
    expect(view.find('.trunk-stage__trunk').exists()).toBe(false);
    expect(view.find('.trunk-stage__wand').exists()).toBe(false);
    expect(view.text()).toContain('Hůlka je uložená. Její ilustrace se připravuje.');
    view.unmount();
  });
});
