import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('./session', async () => {
  const { ref } = await import('vue');
  const account = ref<{ id: string; displayName: string; roles: string[] } | null>({ id: 'admin', displayName: 'Správce', roles: ['Administrator'] });
  return { account, accountOrigin: ref('https://example.test'), accountRequest: api.request,
    hasRole: (role: string) => account.value?.roles.some(value => value === role || value === 'Owner') ?? false };
});
import { account, accountOrigin } from './session';
import WorkshopSettingsPanel from './WorkshopSettingsPanel.vue';

const path = '/app/v1/admin/workshop-settings';
const initial = { revision: 10, dialoguePrompt: 'Uložené pokyny pro rozhovor.', livePrompt: 'Uložené pokyny pro hlas.', reasoningEffort: 'none', speechDirections: 'Přednášej dodaný text.' };
let view: VueWrapper | undefined;
beforeEach(() => {
  account.value = { id: 'admin', displayName: 'Správce', roles: ['Administrator'] };
  accountOrigin.value = 'https://example.test';
  api.request.mockReset().mockResolvedValue(initial);
});
afterEach(() => { view?.unmount(); view = undefined; });
async function render() {
  view = mount(WorkshopSettingsPanel, { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } });
  await flushPromises(); return view;
}
const value = (panel: VueWrapper, selector: string) => (panel.get(selector).element as HTMLTextAreaElement).value;

describe('production workshop settings administration', () => {
  it('loads effective prompts and saves both prompts plus reasoning against the original revision', async () => {
    const panel = await render();
    expect(api.request).toHaveBeenCalledWith(path, 'GET', undefined, expect.any(AbortSignal));
    expect(value(panel, '#workshop-dialogue-prompt')).toBe(initial.dialoguePrompt);
    expect(value(panel, '#workshop-live-prompt')).toBe(initial.livePrompt);
    expect(value(panel, '#workshop-speech-directions')).toBe(initial.speechDirections);
    expect(panel.get('#workshop-speech-directions').attributes('readonly')).toBeDefined();
    expect(panel.text()).toContain('TTS nepřijímá samostatný systémový prompt');
    await panel.get('#workshop-dialogue-prompt').setValue('Nový dialogový prompt.');
    await panel.get('#workshop-live-prompt').setValue('Nový hlasový prompt.');
    await panel.get('#workshop-reasoning').setValue('high');
    api.request.mockResolvedValueOnce({ ...initial, revision: 11, dialoguePrompt: 'Nový dialogový prompt.', livePrompt: 'Nový hlasový prompt.', reasoningEffort: 'high' });
    await panel.get('form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenLastCalledWith(path, 'PUT', {
      expectedRevision: 10, dialoguePrompt: 'Nový dialogový prompt.', livePrompt: 'Nový hlasový prompt.', reasoningEffort: 'high',
    }, expect.any(AbortSignal), expect.stringContaining('Uložit prompty'));
    expect(panel.get('[role="status"]').text()).toContain('Nastavení je uložené');
    expect(panel.get('button[type="submit"]').attributes('disabled')).toBeDefined();
    expect(api.request).toHaveBeenCalledTimes(2);
  });

  it('accepts complete 12000-character prompts without trimming the saved text', async () => {
    const panel = await render();
    const dialoguePrompt = 'Ř'.repeat(12000), livePrompt = 'ž'.repeat(12000);
    await panel.get('#workshop-dialogue-prompt').setValue(dialoguePrompt);
    await panel.get('#workshop-live-prompt').setValue(livePrompt);
    expect(panel.get('button[type="submit"]').attributes('disabled')).toBeUndefined();
    api.request.mockResolvedValueOnce({ ...initial, revision: 11, dialoguePrompt, livePrompt });
    await panel.get('form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenLastCalledWith(path, 'PUT', { expectedRevision: 10, dialoguePrompt, livePrompt, reasoningEffort: 'none' }, expect.any(AbortSignal), expect.any(String));
    expect(value(panel, '#workshop-dialogue-prompt')).toHaveLength(12000);
    expect(panel.find('[role="alert"]').exists()).toBe(false);
  });

  it('preserves conflicting drafts and requires an explicit reload before another save', async () => {
    const panel = await render();
    await panel.get('#workshop-dialogue-prompt').setValue('Neuložená úprava.');
    await panel.get('#workshop-reasoning').setValue('medium');
    api.request.mockRejectedValueOnce(Object.assign(new Error('workshop_settings_conflict'), { status: 409 }));
    await panel.get('form').trigger('submit'); await flushPromises();
    expect(value(panel, '#workshop-dialogue-prompt')).toBe('Neuložená úprava.');
    expect(value(panel, '#workshop-reasoning')).toBe('medium');
    expect(panel.get('[role="alert"]').text()).toContain('Tvoje úpravy zůstaly');
    await panel.get('form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenCalledTimes(2);
    api.request.mockRejectedValueOnce(Object.assign(new Error('unavailable'), { status: 503 }));
    await panel.findAll('button').find(button => button.text() === 'Zahodit úpravy a načíst uložené nastavení')!.trigger('click');
    await flushPromises();
    expect(value(panel, '#workshop-dialogue-prompt')).toBe('Neuložená úprava.');
    await panel.get('form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenCalledTimes(3);
    api.request.mockResolvedValueOnce({ ...initial, revision: 12, dialoguePrompt: 'Úprava jiného správce.' });
    await panel.findAll('button').find(button => button.text() === 'Zahodit úpravy a načíst uložené nastavení')!.trigger('click');
    await flushPromises();
    expect(value(panel, '#workshop-dialogue-prompt')).toBe('Úprava jiného správce.');
    expect(panel.find('[role="alert"]').exists()).toBe(false);
  });

  it.each(['', '   ', 'a'.repeat(12001)])('rejects invalid prompt length or blank content locally', async text => {
    const panel = await render();
    await panel.get('#workshop-dialogue-prompt').setValue(text);
    await panel.get('form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenCalledOnce();
    expect(panel.get('button[type="submit"]').attributes('disabled')).toBeDefined();
  });

  it('preserves a draft after verification rejection and does not replay the save', async () => {
    const panel = await render();
    await panel.get('#workshop-live-prompt').setValue('Neuložený hlas.');
    api.request.mockRejectedValueOnce(Object.assign(new Error('recent_passkey_required'), { status: 403 }));
    await panel.get('form').trigger('submit'); await flushPromises();
    expect(value(panel, '#workshop-live-prompt')).toBe('Neuložený hlas.');
    expect(panel.get('[role="alert"]').text()).toContain('Znovu ověřit přihlášení');
    expect(api.request).toHaveBeenCalledTimes(2);
  });

  it.each(['Tester', 'Customer'])('does not load or expose settings to %s', async role => {
    account.value = { id: 'guest', displayName: 'Host', roles: [role] };
    const panel = await render();
    expect(api.request).not.toHaveBeenCalled(); expect(panel.find('form').exists()).toBe(false);
    expect(panel.text()).not.toContain(initial.dialoguePrompt);
  });

  it('aborts a pending save and ignores its result after an account switch', async () => {
    const panel = await render();
    await panel.get('#workshop-dialogue-prompt').setValue('Neuložená změna.');
    let finish!: (value: unknown) => void;
    api.request.mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    await panel.get('form').trigger('submit');
    const signal = api.request.mock.calls[1][3] as AbortSignal;
    account.value = { id: 'customer', displayName: 'Host', roles: ['Customer'] };
    expect(signal.aborted).toBe(true);
    finish({ ...initial, revision: 11, dialoguePrompt: 'Opožděná odpověď.' }); await flushPromises();
    expect(panel.find('form').exists()).toBe(false);
    expect(panel.text()).not.toContain('Opožděná odpověď.');
    expect(panel.find('[role="status"]').exists()).toBe(false);
  });

  it('ignores a late load from a previous server even for the same administrator ID', async () => {
    let finish!: (value: unknown) => void;
    api.request.mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    const panel = await render();
    const signal = api.request.mock.calls[0][3] as AbortSignal;
    api.request.mockResolvedValueOnce({ ...initial, dialoguePrompt: 'Pokyny nového serveru.' });
    accountOrigin.value = 'https://another.example'; await flushPromises();
    expect(signal.aborted).toBe(true);
    finish({ ...initial, dialoguePrompt: 'Starý server.' }); await flushPromises();
    expect(value(panel, '#workshop-dialogue-prompt')).toBe('Pokyny nového serveru.');
  });

  it('aborts and ignores a pending load when unmounted', async () => {
    let finish!: (value: unknown) => void;
    api.request.mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    await render();
    const signal = api.request.mock.calls[0][3] as AbortSignal;
    view!.unmount(); view = undefined;
    expect(signal.aborted).toBe(true);
    finish(initial); await flushPromises();
    expect(api.request).toHaveBeenCalledOnce();
  });
});
