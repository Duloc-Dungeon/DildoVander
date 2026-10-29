import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { createMemoryHistory, createRouter } from 'vue-router';
import GameYoloStage from './GameYoloStage.vue';
import type { Match } from './game';

const story = {
  status: 'complete',
  beats: ['První dějství.', 'Druhé dějství.', 'Třetí dějství.'].map(text => ({ text, detailRefs: [] })),
  verdict: 'Mira vítězí.',
};
const match = (patch: Partial<Match> = {}): Match => ({
  id: 'a'.repeat(32), mode: 'yolo', status: 'complete', revision: 3,
  opponent: { id: 'two', displayName: 'Eliška', xp: 20, matchesPlayed: 3 },
  rounds: [], completedRounds: 0, totalRounds: 0, block: 1, playerScore: 0, opponentScore: 0,
  playerSparks: 0, opponentSparks: 0, ownChoiceLocked: false, opponentChoiceLocked: false,
  tasksEnabled: false, adultConsent: false, flirtConsent: false, createdAt: 1, xpAwarded: 0,
  result: 'win', yolo: story, ...patch,
});
async function render(value = match()) {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:pathMatch(.*)*', component: { template: '<div />' } }] });
  await router.push('/play');
  return mount(GameYoloStage, { props: { match: value, playerName: 'Mira', busy: false, uncertain: false }, global: { plugins: [router] } });
}

describe('YOLO theatre', () => {
  it('labels synthetic wand artwork visibly and accessibly', async () => {
    const value = match();
    value.opponent.wandSynthetic = true;
    const view = await render(value);
    await view.setProps({ synthetic: true });
    expect(view.get('.yolo-theatre__cast').text().match(/ukázka/g)).toHaveLength(2);
    expect(view.get('.yolo-theatre__cast').attributes('aria-hidden')).toBeUndefined();
    view.unmount();
  });
  it('reveals the verdict only after the third act and retains reading position across polls', async () => {
    const view = await render();
    expect(view.text()).toContain('První dějství.');
    expect(view.text()).not.toContain(story.verdict);
    expect(view.find('[data-result]').exists()).toBe(false);
    expect(view.find('a').exists()).toBe(false);
    await view.findAll('button')[0]!.trigger('click');
    expect(view.text()).toContain('Druhé dějství.');
    expect(view.text()).not.toContain(story.verdict);
    await view.setProps({ match: match({ revision: 4 }) });
    expect(view.get('[aria-current="step"]').text()).toContain('Střet');
    await view.findAll('button')[0]!.trigger('click');
    expect(view.get('.yolo-theatre__verdict').text()).toBe(story.verdict);
    expect(view.get('a').text()).toBe('Odveta');
    expect(view.emitted('revealed')?.at(-1)).toEqual([true]);
    view.unmount();
  });

  it('shows all acts on demand and resets on another match or retry', async () => {
    const view = await render();
    await view.findAll('button')[1]!.trigger('click');
    expect(view.findAll('.yolo-theatre__beat')).toHaveLength(3);
    await view.setProps({ match: match({ id: 'b'.repeat(32) }) });
    expect(view.text()).not.toContain(story.verdict);
    await view.findAll('button')[1]!.trigger('click');
    await view.setProps({ match: match({ status: 'generating', yolo: { ...story, status: 'generating' } }) });
    expect(view.findAll('.yolo-theatre__beat')).toHaveLength(0);
    expect(view.text()).not.toContain(story.verdict);
    await view.setProps({ match: match() });
    expect(view.get('[aria-current="step"]').text()).toContain('Nástup');
    expect(view.emitted('revealed')?.at(-1)).toEqual([false]);
    view.unmount();
  });

  it('does not resurrect cancelled stories or expose incomplete provider output', async () => {
    const view = await render(match({ status: 'cancelled' }));
    expect(view.text()).toContain('Přenos je zrušený.');
    expect(view.text()).not.toContain(story.verdict);
    expect(view.find('button').exists()).toBe(false);
    await view.setProps({ match: match({ yolo: { ...story, beats: story.beats.slice(0, 2) } }) });
    expect(view.text()).not.toContain('První dějství.');
    expect(view.text()).not.toContain(story.verdict);
    view.unmount();
  });

  it('keeps recovery explicit and blocked while the server outcome is uncertain', async () => {
    const view = await render(match({ status: 'generating', yolo: { status: 'failed', beats: [], verdict: '' } }));
    await view.findAll('button')[0]!.trigger('click');
    expect(view.emitted('retry')).toHaveLength(1);
    await view.findAll('button')[1]!.trigger('click');
    expect(view.emitted('cancel')).toHaveLength(1);
    await view.setProps({ uncertain: true });
    expect(view.findAll('button').every(button => button.attributes('disabled') !== undefined)).toBe(true);
    await flushPromises();
    view.unmount();
  });
});
