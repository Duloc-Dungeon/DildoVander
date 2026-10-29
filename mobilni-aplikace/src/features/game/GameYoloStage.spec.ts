import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { createMemoryHistory, createRouter } from 'vue-router';
import GameYoloStage from './GameYoloStage.vue';
import type { Match, YoloScenario } from './game';

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


const scenario = (patch: Partial<YoloScenario> = {}): YoloScenario => ({
  id: 'tea-inspector-v1', title: 'Čaj pro kontrolora', objective: 'Doručit čaj v šálku.', situation: 'Mezi konvicí a stolem je okno.', act: 1,
  ownChoiceLocked: false, opponentChoiceLocked: false, revealed: [],
  options: ['levitate', 'carry', 'slide'].map(id => ({ id, title: `Plán ${id}`, benefit: `Výhoda ${id}`, cost: `Cena ${id}` })), ...patch,
});
const choosingMatch = (value = scenario()) => match({ status: 'active', result: undefined, yolo: { status: 'choosing', beats: [], verdict: '', scenario: value } });

describe('YOLO decisions', () => {
  it('lets a player change cards locally and confirms only once', async () => {
    const view = await render(choosingMatch());
    expect(view.findAll('.yolo-decision__options label')).toHaveLength(3);
    expect(view.find('.yolo-theatre__status').exists()).toBe(false);
    await view.get('input[value="levitate"]').setValue();
    await view.get('input[value="carry"]').setValue();
    expect(view.emitted('choose')).toBeUndefined();
    await view.get('form').trigger('submit');
    await view.get('form').trigger('submit');
    expect(view.emitted('choose')).toEqual([['carry']]);
    expect(view.find('.yolo-theatre__outcome').exists()).toBe(false);
    view.unmount();
  });

  it.each(['busy', 'uncertain'] as const)('blocks confirmation while %s and retains the selected plan through recovery', async state => {
    const view = await render(choosingMatch());
    await view.get('input[value="carry"]').setValue();
    await view.setProps({ [state]: true });
    await view.get('form').trigger('submit');
    expect(view.emitted('choose')).toBeUndefined();
    await view.setProps({ [state]: false });
    expect((view.get('input[value="carry"]').element as HTMLInputElement).checked).toBe(true);
    await view.get('form').trigger('submit');
    expect(view.emitted('choose')).toEqual([['carry']]);
    view.unmount();
  });

  it('resets selection for a new act but preserves already revealed consequences', async () => {
    const view = await render(choosingMatch());
    await view.get('input[value="carry"]').setValue();
    const decision = { id: 'carry', act: 1, choice: 'carry', title: 'Nést šálek', benefit: 'Pevný úchop.', cost: 'Pomalá cesta.', consequence: 'Šálek zůstal v ruce.' };
    const next = scenario({ act: 2, revealed: [{ act: 1, player: decision, opponent: { ...decision, consequence: 'Soupeřův šálek zůstal na tácu.' } }] });
    await view.setProps({ match: choosingMatch(next) });
    expect(view.findAll('input').every(input => !(input.element as HTMLInputElement).checked)).toBe(true);
    expect(view.get('.yolo-decision__record').text()).toContain('Šálek zůstal v ruce.');
    expect(view.get('.yolo-decision__record').text()).toContain('Soupeřův šálek zůstal na tácu.');
    await view.setProps({ match: choosingMatch({ ...next, ownChoiceLocked: true }) });
    expect(view.find('form').exists()).toBe(false);
    expect(view.text()).toContain('Tvůj plán je uzamčený');
    expect(view.find('.yolo-decision__record').exists()).toBe(true);
    view.unmount();
  });

  it('clears a local plan on another match and keeps completed consequences during generation', async () => {
    const view = await render(choosingMatch());
    await view.get('input[value="carry"]').setValue();
    await view.setProps({ match: { ...choosingMatch(), id: 'b'.repeat(32) } });
    expect(view.findAll('input').every(input => !(input.element as HTMLInputElement).checked)).toBe(true);
    const decision = { id: 'carry', act: 3, choice: 'carry', title: 'Nést šálek', benefit: 'Pevný úchop.', cost: 'Pomalá cesta.', consequence: 'Čaj dorazil na stůl.' };
    await view.setProps({ match: match({ status: 'generating', result: undefined,
      yolo: { status: 'generating', beats: [], verdict: '', scenario: scenario({ act: 3, options: [], revealed: [{ act: 3, player: decision, opponent: decision }] }) } }) });
    expect(view.find('form').exists()).toBe(false);
    expect(view.get('.yolo-decision__record').text()).toContain('Čaj dorazil na stůl.');
    expect(view.find('.yolo-theatre__outcome').exists()).toBe(false);
    expect(view.emitted('revealed')?.at(-1)).toEqual([false]);
    view.unmount();
  });

  it('retains the selected card after a failed save has been verified', async () => {
    const view = await render(choosingMatch());
    await view.get('input[value="carry"]').setValue();
    await view.get('form').trigger('submit');
    await view.setProps({ busy: true });
    await view.setProps({ busy: false, uncertain: true });
    await view.get('form').trigger('submit');
    expect(view.emitted('choose')).toHaveLength(1);
    await view.setProps({ uncertain: false });
    await view.get('form').trigger('submit');
    expect(view.emitted('choose')).toEqual([['carry'], ['carry']]);
    view.unmount();
  });

  it.each([['draw', 'Remíza.'], ['fiasco', 'Společné fiasko.']])('reveals the stored %s outcome only with the final verdict', async (outcome, title) => {
    const view = await render(match({ result: 'draw', yolo: { ...story, scenario: scenario({ act: 3, options: [] }), outcome } }));
    expect(view.find('.yolo-theatre__outcome').exists()).toBe(false);
    await view.findAll('.yolo-theatre__reading-controls button')[1]!.trigger('click');
    expect(view.get('.yolo-theatre__outcome').text()).toBe(title);
    expect(view.emitted('revealed')?.at(-1)).toEqual([true]);
    view.unmount();
  });
  it.each([['win', 'Tvůj plán zvítězil.'], ['loss', 'Soupeřův plán zvítězil.']])('reveals viewer-relative %s for outcome winner', async (result, title) => {
    const view = await render(match({ result, yolo: { ...story, scenario: scenario({ act: 3, options: [] }), outcome: 'winner' } }));
    await view.findAll('.yolo-theatre__reading-controls button')[1]!.trigger('click');
    expect(view.get('.yolo-theatre__outcome').text()).toBe(title);
    view.unmount();
  });

});

it('shows AI preparation before any cards or winner exist', async () => {
  const view = await render(match({ status: 'preparing', result: undefined,
    yolo: { status: 'preparing', beats: [], verdict: '' } }));
  expect(view.text()).toContain('Připravujeme váš scénář.');
  expect(view.text()).toContain('obě postavy a jejich hůlky');
  expect(view.find('form').exists()).toBe(false);
  expect(view.find('.yolo-theatre__outcome').exists()).toBe(false);
  view.unmount();
});
