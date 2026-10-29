import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const app = vi.hoisted(() => ({ push: vi.fn(), refresh: vi.fn(), request: vi.fn() }));
vi.mock('vue-router', () => ({ useRouter: () => ({ push: app.push }) }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return { refreshIdentity: app.refresh, accountRequest: app.request, account: ref({ id: 'local-test' }), accountOrigin: ref('') };
});
import { account, accountOrigin } from '../identity/session';
import LocalTryHubView from './LocalTryHubView.vue';

const defaults = {
  provider: 'elevenlabs' as const,
  dialoguePrompt: 'Výchozí dialogový prompt',
  livePrompt: 'Výchozí hlasový prompt',
  speechDirections: 'Přednášej přesně text dodaný GPT.',
};
const initialSettings = {
  revision: 7,
  provider: 'elevenlabs' as const,
  dialoguePrompt: 'Místní dialogový prompt',
  livePrompt: 'Místní hlasový prompt',
};
const experimentResponse = (settings: Omit<typeof initialSettings, 'provider'> & { provider: 'elevenlabs' | 'gpt-live' } = initialSettings) => ({ settings, defaults, loadError: null });
const jsonResponse = (body: unknown, status = 200) => ({
  ok: status >= 200 && status < 300,
  status,
  json: vi.fn().mockResolvedValue(body),
}) as unknown as Response;

beforeEach(() => {
  vi.clearAllMocks();
  account.value = { id: 'local-test', displayName: 'Local', roles: [] };
  accountOrigin.value = '';
  app.request.mockReset().mockResolvedValue({ profile: { profileId: 'profile/test', revision: 3, userInput: { name: 'Testovací kouzelník' } } });
  app.push.mockResolvedValue(undefined);
  app.refresh.mockResolvedValue(undefined);
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(experimentResponse())));
});
afterEach(() => vi.unstubAllGlobals());

async function mountLoadedHub() {
  const view = mount(LocalTryHubView);
  await flushPromises();
  return view;
}

describe('local character reset', () => {
  const press = async (view: ReturnType<typeof mount>, label: string) => {
    await view.findAll('button').find(button => button.text() === label)!.trigger('click');
    await flushPromises();
  };

  it('loads the current local character and requires confirmation before deleting', async () => {
    const view = await mountLoadedHub();
    await press(view, 'Resetovat testovací postavu');
    expect(fetch).toHaveBeenLastCalledWith('/local-try-api/session', expect.objectContaining({ method: 'POST' }));
    expect(app.refresh).toHaveBeenCalledOnce();
    expect(app.request.mock.calls.map(call => call[1])).toEqual(['GET']);
    expect(view.get('[aria-label="Potvrzení resetu postavy"]').text()).toContain('Testovací kouzelník');
    await press(view, 'Zrušit');
    expect(view.find('[aria-label="Potvrzení resetu postavy"]').exists()).toBe(false);
    expect(app.request).toHaveBeenCalledOnce();
    view.unmount();
  });

  it('deletes only the confirmed profile revision and preserves prompt drafts', async () => {
    const view = await mountLoadedHub();
    await view.get('#local-dialogue-prompt').setValue('Neuložený místní prompt');
    await press(view, 'Resetovat testovací postavu');
    app.request.mockResolvedValueOnce(undefined);
    await press(view, 'Potvrdit reset postavy');
    expect(app.request).toHaveBeenLastCalledWith('/app/v1/customer/character?expectedRevision=3&expectedProfileId=profile%2Ftest', 'DELETE', undefined, expect.any(AbortSignal));
    expect(view.text()).toContain('Postava byla resetována');
    expect((view.get('#local-dialogue-prompt').element as HTMLTextAreaElement).value).toBe('Neuložený místní prompt');
    expect(app.push).not.toHaveBeenCalled();
    expect(fetch).not.toHaveBeenCalledWith('/local-try-api/experiments', expect.anything());
    view.unmount();
  });

  it('requires another preview after a stale or failed delete', async () => {
    const view = await mountLoadedHub();
    await press(view, 'Resetovat testovací postavu');
    app.request.mockRejectedValueOnce(new Error('character_revision_conflict'));
    await press(view, 'Potvrdit reset postavy');
    expect(view.get('[role="alert"]').text()).toContain('postava mezitím změnila');
    expect(view.find('[aria-label="Potvrzení resetu postavy"]').exists()).toBe(false);
    expect(view.text()).not.toContain('Postava byla resetována');
    view.unmount();
  });

  it('does not delete after an account switch or use another backend origin', async () => {
    const view = await mountLoadedHub();
    await press(view, 'Resetovat testovací postavu');
    account.value = { ...account.value!, id: 'different-account' };
    await press(view, 'Potvrdit reset postavy');
    expect(app.request.mock.calls.map(call => call[1])).toEqual(['GET']);
    accountOrigin.value = 'https://production.example';
    await press(view, 'Resetovat testovací postavu');
    expect(app.request).toHaveBeenCalledOnce();
    view.unmount();
  });

  it('handles an empty local account without issuing a delete', async () => {
    app.request.mockResolvedValueOnce({ profile: null });
    const view = await mountLoadedHub();
    await press(view, 'Resetovat testovací postavu');
    expect(view.text()).toContain('Žádná testovací postava není připravená');
    expect(view.find('[aria-label="Potvrzení resetu postavy"]').exists()).toBe(false);
    expect(app.request.mock.calls.map(call => call[1])).toEqual(['GET']);
    view.unmount();
  });
});

describe('local try hub direct access', () => {
  it.each([
    ['Otevřít Kufrovu dílnu', '/play/wand'],
    ['Otevřít AI Persona Lab', '/persona-lab'],
  ])('prepares the local session before entering %s', async (label, path) => {
    const view = await mountLoadedHub();
    await view.findAll('button').find(button => button.text() === label)!.trigger('click');
    await flushPromises();
    expect(fetch).toHaveBeenCalledWith('/local-try-api/session', expect.objectContaining({
      method: 'POST', credentials: 'same-origin', cache: 'no-store',
    }));
    expect(app.refresh).toHaveBeenCalledOnce();
    expect(app.push).toHaveBeenCalledWith(path);
    expect(vi.mocked(fetch).mock.invocationCallOrder[1]).toBeLessThan(app.refresh.mock.invocationCallOrder[0]);
    expect(app.refresh.mock.invocationCallOrder[0]).toBeLessThan(app.push.mock.invocationCallOrder[0]);
    expect(view.find('[role="alert"]').exists()).toBe(false);
    view.unmount();
  });

  it('shows an actionable local setup error without sending the guest to login', async () => {
    const view = await mountLoadedHub();
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse({}, 503));
    await view.findAll('button').find(button => button.text() === 'Otevřít Kufrovu dílnu')!.trigger('click');
    await flushPromises();
    expect(view.get('[role="alert"]').text()).toContain('Spusť místní hub jeho spouštěčem');
    expect(app.push).not.toHaveBeenCalled();
    expect(app.refresh).not.toHaveBeenCalled();
    view.unmount();
  });

  it('does not create duplicate sessions while entry is being prepared', async () => {
    const view = await mountLoadedHub();
    let finish!: (response: Response) => void;
    vi.mocked(fetch).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    await view.findAll('button').find(button => button.text() === 'Otevřít Kufrovu dílnu')!.trigger('click');
    expect(view.get('[role="status"]').text()).toContain('Připravujeme místní vstup');
    expect(view.findAll('button').filter(button => button.text().startsWith('Otevřít')).every(button => button.attributes('disabled') !== undefined)).toBe(true);
    await view.findAll('button').find(button => button.text() === 'Otevřít AI Persona Lab')!.trigger('click');
    expect(fetch).toHaveBeenCalledTimes(2);
    finish(jsonResponse({})); await flushPromises();
    expect(app.push).toHaveBeenCalledOnce();
    view.unmount();
  });
});

describe('local conversation experiments', () => {
  it('loads the applied provider and prompts through the local same-origin API', async () => {
    const view = await mountLoadedHub();
    expect(fetch).toHaveBeenCalledWith('/local-try-api/experiments/read', expect.objectContaining({
      method: 'POST', credentials: 'same-origin', cache: 'no-store', body: '{}',
    }));
    expect(view.get('.local-try-hub__applied').text()).toContain('ElevenLabs v4 + GPT');
    expect((view.get('#local-dialogue-prompt').element as HTMLTextAreaElement).value).toBe(initialSettings.dialoguePrompt);
    expect((view.get('#local-live-prompt').element as HTMLTextAreaElement).value).toBe(initialSettings.livePrompt);
    view.unmount();
  });

  it('saves both prompt drafts and the selected provider against the loaded revision', async () => {
    const view = await mountLoadedHub();
    await view.get('input[value="gpt-live"]').setValue();
    await view.get('#local-dialogue-prompt').setValue('Upravený dialog');
    await view.get('#local-live-prompt').setValue('Upravený hlas');
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse({
      settings: { revision: 8, provider: 'gpt-live', dialoguePrompt: 'Upravený dialog', livePrompt: 'Upravený hlas' },
      defaults,
      loadError: null,
    }));
    await view.findAll('button').find(button => button.text() === 'Použít pro další připojení')!.trigger('click');
    await flushPromises();
    expect(fetch).toHaveBeenLastCalledWith('/local-try-api/experiments', expect.objectContaining({
      method: 'POST', credentials: 'same-origin', cache: 'no-store',
      body: JSON.stringify({
        expectedRevision: 7, provider: 'gpt-live', dialoguePrompt: 'Upravený dialog', livePrompt: 'Upravený hlas',
      }),
    }));
    expect(view.get('.local-try-hub__applied').text()).toContain('GPT-Live');
    expect(view.get('.local-try-hub__help').text()).toContain('Probíhající rozhovory zůstanou beze změny');
    expect(view.get('.local-try-hub__notice').text()).toContain('probíhající rozhovor se nemění');
    expect(view.findAll('textarea:not([readonly])').every(textarea => textarea.attributes('maxlength') === '12000')).toBe(true);
    expect(view.get('#local-speech-directions').attributes('readonly')).toBeDefined();
    expect((view.get('#local-speech-directions').element as HTMLTextAreaElement).value).toBe(defaults.speechDirections);
    view.unmount();
  });

  it('fills defaults without saving until the explicit apply action', async () => {
    const view = await mountLoadedHub();
    await view.findAll('button').find(button => button.text() === 'Výchozí prompty')!.trigger('click');
    expect((view.get('#local-dialogue-prompt').element as HTMLTextAreaElement).value).toBe(defaults.dialoguePrompt);
    expect((view.get('#local-live-prompt').element as HTMLTextAreaElement).value).toBe(defaults.livePrompt);
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(view.findAll('button').find(button => button.text() === 'Použít pro další připojení')!.attributes('disabled')).toBeUndefined();
    view.unmount();
  });

  it('keeps unsaved edits after a stale revision and requires explicit reload', async () => {
    const view = await mountLoadedHub();
    await view.get('#local-dialogue-prompt').setValue('Moje neuložená změna');
    await view.findAll('button').find(button => button.text() === 'Načíst aktuální')!.trigger('click');
    expect(fetch).toHaveBeenCalledTimes(1);
    expect((view.get('#local-dialogue-prompt').element as HTMLTextAreaElement).value).toBe('Moje neuložená změna');

    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse({}, 409));
    await view.findAll('button').find(button => button.text() === 'Použít pro další připojení')!.trigger('click');
    await flushPromises();
    expect(view.get('[role="alert"]').text()).toContain('Neuložené úpravy zůstaly zachované');
    expect((view.get('#local-dialogue-prompt').element as HTMLTextAreaElement).value).toBe('Moje neuložená změna');

    const latest = { revision: 8, provider: 'gpt-live' as const, dialoguePrompt: 'Nová uložená verze', livePrompt: 'Nový hlasový prompt' };
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse(experimentResponse(latest)));
    await view.findAll('button').find(button => button.text().includes('Načíst aktuální nastavení a zrušit'))!.trigger('click');
    await flushPromises();
    expect((view.get('#local-dialogue-prompt').element as HTMLTextAreaElement).value).toBe(latest.dialoguePrompt);
    expect(view.get('.local-try-hub__applied').text()).toContain('revize 8');
    expect(view.find('[role="alert"]').exists()).toBe(false);
    view.unmount();
  });

  it('shows a local setup error if prompt settings cannot be read', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse({ settings: null, defaults, loadError: 'unavailable' }));
    const view = await mountLoadedHub();
    expect(view.get('[role="alert"]').text()).toContain('Spusť lokální hub jeho spouštěčem');
    expect(view.get('fieldset').attributes('disabled')).toBeDefined();
    view.unmount();
  });
});
