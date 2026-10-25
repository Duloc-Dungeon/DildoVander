import { mount } from '@vue/test-utils';
import { expect, it } from 'vitest';
import DuelPartyTaskPanel from './DuelPartyTaskPanel.vue';

const task = { id: 'press-conference', title: 'Tisková konference poraženého',
  text: 'Porazený vysvětlí prohru jako administrativní omyl. Vítěz položí jednu otázku.',
  alternative: 'Ze sedu proneseš jedinou slavnostní větu.',
  alternativeRoast: 'Výbor pro sraby schválil tvou žádost. Olivander už je pod stolem.', involvesWinner: true };

it('offers a reversible easier choice with its own roast without changing the saved task', async () => {
  const original = structuredClone(task);
  const view = mount(DuelPartyTaskPanel, { props: { task, loser: 'Alžběta', winner: 'Boris' } });
  try {
    expect(view.text()).toContain('Alžběta');
    expect(view.text()).toContain('Boris');
    expect(view.get('.duel-party-task__reply').text()).toBe('');
    await view.findAll('button').find(button => button.text() === 'Zvolit lehčí variantu')!.trigger('click');
    expect(view.get('.duel-party-task__reply').text()).toContain('Výbor pro sraby');
    expect(view.get('.duel-party-task__reply').text()).toContain(task.alternative);
    await view.findAll('button').find(button => button.text() === 'Dnes vynechám')!.trigger('click');
    expect(view.get('.duel-party-task__reply').text()).toContain('Velkolepých záměrů');
    expect(view.get('.duel-party-task__reply').text()).not.toContain('Výbor pro sraby');
    expect(task).toEqual(original);
  } finally { view.unmount(); }
});
