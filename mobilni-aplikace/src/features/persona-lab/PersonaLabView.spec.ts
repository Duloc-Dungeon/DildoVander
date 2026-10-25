import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import PersonaLabView from './PersonaLabView.vue';
import { account } from '../identity/session';
import { metricLabels, type LabSession } from './contracts';
import type { VoiceObserver } from './voice';
import { directorStop } from '../director/directorStop';

const api = vi.hoisted(() => ({ transport: vi.fn(), voiceConnect: vi.fn(), voiceDispose: vi.fn(), voiceEnd: vi.fn(), prepareImage: vi.fn(),
  voiceObserver: null as VoiceObserver | null }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue'); const account = ref({ id: 'tester', displayName: 'Tester', roles: ['Tester'] });
  return { account, accountOrigin: ref('https://lab.example'), accountTransport: api.transport,
    hasRole: (role: string) => account.value?.roles.includes(role) || account.value?.roles.includes('Owner') };
});
vi.mock('./voice', () => ({ LabVoice: class {
  constructor(observer: NonNullable<typeof api.voiceObserver>) { api.voiceObserver = observer; }
  connect = api.voiceConnect; dispose = api.voiceDispose; end = api.voiceEnd; setMuted = vi.fn(); interrupt = vi.fn();
} }));
vi.mock('./image', () => ({ prepareLabImage: api.prepareImage }));
vi.mock('../suitcase/deviceSession', async () => {
  const { ref } = await import('vue');
  return { deviceSession: { native: false, snapshot: ref(null), savedDevice: ref({ paired: false }),
    startLabVoice: vi.fn(() => false), stopLabVoice: vi.fn(() => false), labConnectionEpoch: () => 1, configureVoice: vi.fn(() => false) } };
});
vi.mock('../suitcase/deviceAssociation', async () => { const { ref } = await import('vue'); return { deviceAssociation: { canOperate: ref(false), refresh: vi.fn(async () => {}) } }; });
const revision = { id: 'revision', parentId: null, name: 'Základ', instructions: 'Testovací instrukce', engine: 'gpt-live-1', voice: 'cedar', status: 'baseline', sourceVersion: 'dv01-live-v1', createdAt: '2026-10-20T10:00:00Z', publishedAt: null, revision: 1 };
const fixture = { id: 'empty', name: 'Prázdná paměť', description: 'Syntetická testovací paměť', current: '', evening: '', returning: '' };
const scenario = { id: 'intro', name: 'První setkání', description: 'Syntetický scénář', openingText: 'Dobrý večer.' };
let session: LabSession;
const wrappers: ReturnType<typeof mount>[] = [];
beforeEach(() => {
  api.transport.mockReset(); api.voiceConnect.mockReset(); api.voiceDispose.mockReset(); api.voiceEnd.mockReset(); api.prepareImage.mockReset();
  account.value = { id: 'tester', displayName: 'Tester', roles: ['Tester'] };
  session = { id: 'session', label: 'Můj syntetický test', ownerId: 'tester', createdAt: '2026-10-20T10:00:00Z', endedAt: null, status: 'open',
    engine: 'gpt-live-1', scenarioId: 'intro', fixtureId: 'empty', platform: 'web', executionPlatforms: ['web'], recordContent: false, config: revision,
    turns: [{ id: 'turn-1', role: 'user', text: 'Syntetický vstup', createdAt: '2026-10-20T10:00:00Z' },
      { id: 'turn-2', role: 'assistant', text: 'Syntetická odpověď', createdAt: '2026-10-20T10:00:01Z', latencyMs: 123 }], events: [] } as LabSession;
  api.transport.mockImplementation(async (path: string, init: RequestInit) => {
    let data: unknown;
    if (path.endsWith('/catalog')) data = { activeRevisionId: 'revision', versions: [revision], scenarios: [scenario], memoryFixtures: [fixture], capabilities: { live: true, realtime: false, voice: true, text: true, simulationOnly: true } };
    else if (path.endsWith('/analytics')) data = { sessions: 1, evaluations: 0, metrics: [], comparisons: [] };
    else if (path.endsWith('/suites')) data = [];
    else if (path.endsWith('/sessions') && init.method === 'GET') data = [session];
    else if (path.endsWith('/sessions') && init.method === 'POST') data = { ...session, turns: [], ...JSON.parse(init.body as string) };
    else if (path.endsWith('/evaluation')) { session = { ...session, evaluation: JSON.parse(init.body as string) }; data = session; }
    else if (path.endsWith('/turns')) data = session;
    else if (path.endsWith('/end')) data = { ...session, status: 'ended' };
    else throw new Error(`Unexpected test request ${path}`);
    return new Response(JSON.stringify(data), { status: 200 });
  });
});
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers(); vi.restoreAllMocks(); });
async function render(consent = true) {
  const wrapper = mount(PersonaLabView, { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } });
  wrappers.push(wrapper); await flushPromises();
  if (consent) await wrapper.get('#lab-provider-consent').setValue(true);
  return wrapper;
}
function button(wrapper: ReturnType<typeof mount>, label: string) {
  return wrapper.findAll('button').find(item => item.text() === label)!;
}
function enableImages(): void {
  const baseline = api.transport.getMockImplementation()!;
  api.transport.mockImplementation(async (path: string, init: RequestInit) => {
    const response = await baseline(path, init);
    if (!path.endsWith('/catalog')) return response;
    const data = await response.json(); data.capabilities.realtime = true; data.capabilities.imageRealtime = true;
    return new Response(JSON.stringify(data));
  });
}
async function choosePhoto(wrapper: ReturnType<typeof mount>, selector: string): Promise<void> {
  const input = wrapper.get(selector);
  Object.defineProperty(input.element, 'files', { configurable: true, value: [new File(['synthetic'], 'test.jpg', { type: 'image/jpeg' })] });
  await input.trigger('change'); await flushPromises();
}
async function startSpokenVoice(wrapper: ReturnType<typeof mount>): Promise<void> {
  api.voiceConnect.mockImplementation(async () => { api.voiceObserver!.phase('listening'); });
  await button(wrapper, 'Spustit hlas').trigger('click'); await flushPromises();
}
describe('Persona Lab user journeys', () => {
  it('requires unchecked recipient/data/purpose consent before session, text or voice submission', async () => {
    const wrapper = await render(false);
    expect(wrapper.get('#lab-provider-consent').element).toHaveProperty('checked', false);
    expect(wrapper.text()).toContain('společnosti OpenAI');
    expect(wrapper.text()).toContain('zvuk mikrofonu');
    await wrapper.get('.lab-setup').trigger('submit'); await flushPromises();
    expect(api.transport.mock.calls.some(([, init]) => init.method === 'POST')).toBe(false);
    await wrapper.get('#lab-existing').setValue('session');
    await wrapper.get('#lab-message').setValue('Test bez souhlasu');
    await wrapper.get('.lab-composer').trigger('submit');
    await button(wrapper, 'Spustit hlas').trigger('click'); await flushPromises();
    expect(api.voiceConnect).not.toHaveBeenCalled();
    expect(api.transport.mock.calls.some(([path]) => path.endsWith('/turns'))).toBe(false);
    expect(button(wrapper, 'Odeslat do modelu').attributes('disabled')).toBeDefined();
    await wrapper.get('#lab-provider-consent').setValue(true);
    await wrapper.get('.lab-composer').trigger('submit'); await flushPromises();
    expect(api.transport.mock.calls.some(([path]) => path.endsWith('/turns'))).toBe(true);
  });

  it('resets provider consent when the account changes while preserving separate transcript opt-in', async () => {
    const wrapper = await render();
    expect(wrapper.get('#lab-provider-consent').element).toHaveProperty('checked', true);
    account.value = {id: 'next-tester', displayName: 'Next tester', roles: ['Tester']};
    await flushPromises();
    expect(wrapper.get('#lab-provider-consent').element).toHaveProperty('checked', false);
    expect(wrapper.get('.lab-setup input[type="checkbox"]').element).toHaveProperty('checked', false);
  });

  it('participates in the application Stop and releases that participant on navigation', async () => {
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    expect(directorStop.value?.canStop()).toBe(false);
    api.voiceObserver!.phase('listening'); await flushPromises();
    expect(directorStop.value?.canStop()).toBe(true);
    await directorStop.value!.stop(); await flushPromises();
    expect(api.transport.mock.calls.some(([path]) => path.endsWith('/end'))).toBe(true);
    expect(directorStop.value?.canStop()).toBe(false);
    wrappers.splice(wrappers.indexOf(wrapper), 1); wrapper.unmount();
    expect(directorStop.value).toBeNull();
  });
  it('mounts unpaid and uses real configured model labels with recording off', async () => {
    const wrapper = await render();
    expect(api.transport.mock.calls.every(([, init]) => init.method === 'GET')).toBe(true);
    expect(api.voiceConnect).not.toHaveBeenCalled(); expect(wrapper.text()).toContain('SIMULACE');
    expect(wrapper.find('#lab-engine').exists()).toBe(false);
    expect(wrapper.text()).toContain('DV01 · GPT Live');
    await wrapper.get('.lab-setup').trigger('submit'); await flushPromises();
    const request = api.transport.mock.calls.find(([path, init]) => path.endsWith('/sessions') && init.method === 'POST');
    expect(JSON.parse(request![1].body)).toMatchObject({ recordContent: false, engine: 'gpt-live-1', scenarioId: 'intro', fixtureId: 'empty' });
    expect(api.voiceConnect).not.toHaveBeenCalled(); expect(wrapper.text()).toContain('Žádná odpověď zatím nevznikla');
  });
  it('saves exactly seven human ratings and comments bound to exact turn ids', async () => {
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    for (const key of Object.keys(metricLabels)) await wrapper.get(`input[name="lab-score-${key}"][value="4"]`).setValue(true);
    await wrapper.get('#lab-comment-turn-2').setValue('Tady chybí pointa.'); await wrapper.get('.lab-evaluation').trigger('submit'); await flushPromises();
    const request = api.transport.mock.calls.find(([path]) => path.endsWith('/evaluation'));
    const body = JSON.parse(request![1].body);
    expect(Object.keys(body.scores)).toHaveLength(7); expect(body.scores.personaConsistency).toBe(4);
    expect(body.turnComments).toEqual([{ turnId: 'turn-2', comment: 'Tady chybí pointa.' }]);
  });
  it('sends text only on explicit submit and preserves the prepared session id', async () => {
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    await wrapper.get('#lab-message').setValue('Jak se jmenuješ?');
    expect(api.transport.mock.calls.some(([path]) => path.endsWith('/turns'))).toBe(false);
    await wrapper.get('.lab-composer').trigger('submit'); await flushPromises();
    expect(api.transport).toHaveBeenCalledWith('/app/v1/persona-lab/sessions/session/turns', expect.objectContaining({ method: 'POST', body: '{"text":"Jak se jmenuješ?","platform":"web"}' }));
    expect(wrapper.text()).toContain('Syntetická odpověď');
  });
  it('keeps admin configuration out of tester UI and clears private state on logout', async () => {
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    expect(wrapper.find('#lab-configuration-title').exists()).toBe(false);
    account.value = null; await flushPromises();
    expect(wrapper.text()).not.toContain('Syntetická odpověď'); expect(wrapper.text()).toContain('Nejprve se přihlaste');
    expect(api.voiceDispose).toHaveBeenCalled();
  });
  it('lets Stop abort a pending text response and ignores its late result', async () => {
    const baseline = api.transport.getMockImplementation()!;
    let complete!: (response: Response) => void;
    api.transport.mockImplementation((path: string, init: RequestInit) => path.endsWith('/turns')
      ? new Promise<Response>(resolve => { complete = resolve; }) : baseline(path, init));
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    await wrapper.get('#lab-message').setValue('Dlouhá otázka'); await wrapper.get('.lab-composer').trigger('submit'); await flushPromises();
    const pending = api.transport.mock.calls.find(([path]) => path.endsWith('/turns'));
    const stopButton = wrapper.findAll('button').find(button => button.text() === 'Ukončit test')!;
    expect(stopButton.attributes('disabled')).toBeUndefined(); await stopButton.trigger('click'); await flushPromises();
    expect(pending![1].signal.aborted).toBe(true);
    complete(new Response(JSON.stringify({ ...session, label: 'Obsolete response' }), { status: 200 })); await flushPromises();
    expect(wrapper.text()).toContain('Sezení ukončené'); expect(wrapper.text()).not.toContain('Obsolete response');
  });
  it('creates a new admin draft and publishes only with the observed active version and explicit confirmation', async () => {
    account.value = { id: 'tester', displayName: 'Owner', roles: ['Owner'] };
    const baseline = api.transport.getMockImplementation()!;
    const versions = [revision]; let activeRevisionId = 'revision';
    api.transport.mockImplementation(async (path: string, init: RequestInit) => {
      if (path.endsWith('/catalog')) return new Response(JSON.stringify({ activeRevisionId, versions, scenarios: [scenario], memoryFixtures: [fixture], capabilities: { live: true, realtime: false, voice: true, text: true, simulationOnly: true } }));
      if (path.endsWith('/revisions') && init.method === 'POST') {
        const draft = { ...revision, ...JSON.parse(init.body as string), id: 'draft', status: 'draft', revision: 2 }; versions.push(draft);
        return new Response(JSON.stringify(draft));
      }
      if (path.endsWith('/revisions/draft/publish')) { activeRevisionId = 'draft'; return new Response(JSON.stringify(versions[1])); }
      return baseline(path, init);
    });
    const wrapper = await render();
    await wrapper.findAll('button').find(button => button.text() === 'Konfigurace')!.trigger('click');
    await wrapper.get('#lab-draft-name').setValue('Nový suchý humor');
    await wrapper.get('#lab-draft-instructions').setValue('Používej konkrétní suchý humor.');
    await wrapper.get('.lab-config').trigger('submit'); await flushPromises();
    const draft = api.transport.mock.calls.find(([path, init]) => path.endsWith('/revisions') && init.method === 'POST');
    expect(draft![1].headers['X-DV-Confirm']).toBe('yes'); expect(JSON.parse(draft![1].body).parentId).toBe('revision');
    let publish = wrapper.findAll('button').find(button => button.text() === 'Publikovat vybraný draft')!;
    expect(publish.attributes('disabled')).toBeDefined();
    await wrapper.get('.lab-publish input[type="checkbox"]').setValue(true); await publish.trigger('click'); await flushPromises();
    const request = api.transport.mock.calls.find(([path]) => path.endsWith('/revisions/draft/publish'));
    expect(JSON.parse(request![1].body)).toEqual({ expectedActiveRevisionId: 'revision' });
    expect(wrapper.text()).toContain('Verze byla publikována');
  });
  it('keeps historical Realtime sessions readable and blocks new dialogue', async () => {
    session.engine = 'gpt-realtime-2.1'; session.turns[0]!.text = 'Historická odpověď';
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    expect(wrapper.text()).toContain('Historická odpověď');
    expect(wrapper.text()).toContain('archivované sezení');
    expect(wrapper.find('#lab-engine').exists()).toBe(false);
    expect(wrapper.get('#lab-message').attributes('disabled')).toBeDefined();
    await wrapper.get('.lab-composer').trigger('submit'); await flushPromises();
    expect(api.transport.mock.calls.filter(([path]) => path.endsWith('/turns'))).toHaveLength(0);
  });
  it('keeps old GPT Live sessions and revisions readable but prevents execution', async () => {
    const oldRevision = { ...revision, id: 'old-live', name: 'Předchozí DV01', engine: 'gpt-live-1' as const,
      status: 'baseline' as const, sourceVersion: 'dv01-legacy-v1' };
    session.config = oldRevision;
    session.turns[0]!.text = 'Odpověď z archivované revize';
    const baseTransport = api.transport.getMockImplementation()!;
    api.transport.mockImplementation(async (path: string, init: RequestInit) => {
      const response = await baseTransport(path, init);
      if (!path.endsWith('/catalog')) return response;
      const catalog = await response.json(); catalog.versions.push(oldRevision);
      return new Response(JSON.stringify(catalog), { status: 200 });
    });
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    expect(wrapper.text()).toContain('Odpověď z archivované revize');
    expect(wrapper.text()).toContain('archivované sezení');
    expect(wrapper.get('#lab-message').attributes('disabled')).toBeDefined();
    expect(wrapper.findAll('button').some(item => item.text() === 'Spustit hlas')).toBe(false);
    expect(directorStop.value?.canStop()).toBe(false);
    expect(Array.from((wrapper.get('#lab-revision').element as HTMLSelectElement).options).map(item => item.value)).toEqual(['revision']);
    await wrapper.get('#lab-message').setValue('Nemělo by se odeslat');
    await wrapper.get('.lab-composer').trigger('submit'); await flushPromises();
    expect(api.transport.mock.calls.filter(([path]) => path.endsWith('/turns'))).toHaveLength(0);
    expect(api.voiceConnect).not.toHaveBeenCalled();
  });
  it('keeps historical image withdrawal available without reopening Realtime', async () => {
    session.engine = 'gpt-realtime-2.1'; session.hasImage = true;
    const baseline = api.transport.getMockImplementation()!;
    api.transport.mockImplementation((path: string, init: RequestInit) => path.endsWith('/image') && init.method === 'DELETE'
      ? new Response(JSON.stringify({ ...session, hasImage: false })) : baseline(path, init));
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    await button(wrapper, 'Odebrat fotografii z testu').trigger('click'); await flushPromises();
    expect(api.transport.mock.calls.filter(([path, init]) => path.endsWith('/image') && init.method === 'DELETE')).toHaveLength(1);
    expect(wrapper.text()).toContain('Server potvrdil odebrání fotografie');
    expect(wrapper.findAll('button').some(item => item.text() === 'Odebrat fotografii z testu')).toBe(false);
  });
  it('ends only the active owned session for a split Live user command without double-counting its first chunk', async () => {
    vi.useFakeTimers();
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    await startSpokenVoice(wrapper);
    const observer = api.voiceObserver!;
    observer.turn({ id: 'spoken', role: 'user', text: 'ukon', createdAt: '' });
    observer.transcript('user', 'ukon'); observer.transcript('user', 'či test');
    await vi.advanceTimersByTimeAsync(500); await flushPromises();
    expect(api.voiceEnd).toHaveBeenCalledTimes(1);
    expect(api.transport.mock.calls.filter(([path]) => path.endsWith('/end')).map(([path]) => path)).toEqual(['/app/v1/persona-lab/sessions/session/end']);
    observer.phase('listening'); observer.transcript('user', 'stop');
    await vi.advanceTimersByTimeAsync(500); await flushPromises();
    expect(api.voiceEnd).toHaveBeenCalledTimes(1);
  });
  it('ignores assistant text, inactive callbacks, negated user text and old voice contexts', async () => {
    vi.useFakeTimers();
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    api.voiceObserver!.transcript('user', 'stop');
    await startSpokenVoice(wrapper);
    const obsolete = api.voiceObserver!;
    obsolete.transcript('assistant', 'ukonči test');
    obsolete.turn({ id: 'negative', role: 'user', text: '', createdAt: '' });
    obsolete.transcript('user', 'neukončuj test');
    await vi.advanceTimersByTimeAsync(500);
    await button(wrapper, 'Vypnout hlas').trigger('click'); await flushPromises();
    await startSpokenVoice(wrapper);
    obsolete.phase('listening'); obsolete.turn({ id: 'old', role: 'assistant', text: 'stop', decision: 'no_selection', createdAt: '' });
    obsolete.transcript('user', 'stop');
    api.voiceObserver!.turn({ id: 'new', role: 'user', text: '', createdAt: '' });
    api.voiceObserver!.transcript('user', 'ukonči');
    api.voiceObserver!.turn({ id: 'next', role: 'user', text: '', createdAt: '' });
    api.voiceObserver!.transcript('user', ' test');
    await vi.advanceTimersByTimeAsync(500); await flushPromises();
    expect(api.transport.mock.calls.some(([path]) => path.endsWith('/end'))).toBe(false);
  });
  it('keeps spoken commands out of hardware-bound contexts and ignores nonterminal assistant decisions', async () => {
    vi.useFakeTimers();
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session'); await startSpokenVoice(wrapper);
    const observer = api.voiceObserver!;
    observer.turn({ id: 'ask', role: 'assistant', text: 'ukonči test', decision: 'ask', createdAt: '' });
    observer.turn({ id: 'propose', role: 'assistant', text: 'stop', decision: 'propose', compartment: 1, createdAt: '' });
    observer.session({ ...session, hardware: { deviceId: 'case', bootId: 'boot', expectedRevision: 1, conversationId: null, status: 'prepared', expiresAt: '' } });
    observer.turn({ id: 'hardware-user', role: 'user', text: '', createdAt: '' }); observer.transcript('user', 'stop');
    await vi.advanceTimersByTimeAsync(500); await flushPromises();
    expect(api.transport.mock.calls.some(([path]) => path.endsWith('/end'))).toBe(false);
    expect(api.voiceEnd).not.toHaveBeenCalled();
  });
  it('releases local voice even when socket End fails and still submits the authenticated session End', async () => {
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session'); await startSpokenVoice(wrapper);
    api.voiceEnd.mockImplementationOnce(() => { throw new Error('Socket closed'); });
    api.voiceObserver!.turn({ id: 'terminal', role: 'assistant', text: 'Hotovo.', decision: 'no_selection', createdAt: '' }); await flushPromises();
    expect(api.voiceDispose).toHaveBeenCalled();
    expect(api.transport.mock.calls.filter(([path]) => path.endsWith('/end'))).toHaveLength(1);
    expect(wrapper.text()).toContain('Sezení ukončené');
  });
  it('does not claim model audio delivery while browser permission or connection is pending', async () => {
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    api.voiceObserver!.phase('connecting'); await flushPromises();
    expect(wrapper.text()).toContain('Připojení k modelu zatím není potvrzené.');
    expect(wrapper.text()).not.toContain('Mikrofon je zapnutý');
    expect(wrapper.text()).not.toContain('odesílá zvuk do testovacího modelu');
    api.voiceObserver!.phase('listening'); await flushPromises();
    expect(wrapper.text()).toContain('Mikrofon je zapnutý.');
    expect(wrapper.text()).not.toContain('Připojení k modelu zatím není potvrzené.');
    api.voiceObserver!.phase('speaking'); await flushPromises();
    expect(wrapper.text()).toContain('Mikrofon je zapnutý.');
    expect(wrapper.text()).not.toContain('odesílá zvuk do testovacího modelu');
    api.voiceObserver!.phase('waiting'); await flushPromises();
    expect(wrapper.text()).toContain('Čekám na připravenost modelu');
    expect(wrapper.text()).toContain('Připravenost modelu k dalšímu vstupu zatím není potvrzená.');
    expect(wrapper.text()).not.toContain('Mikrofon aktivní · naslouchám');
    expect(wrapper.get('#lab-existing').attributes('disabled')).toBeDefined();
    expect(wrapper.findAll('button').some(button => button.text() === 'Spustit hlas')).toBe(false);
    expect(wrapper.findAll('button').some(button => button.text() === 'Vypnout hlas')).toBe(true);
    expect(wrapper.get('.lab-composer textarea').attributes('disabled')).toBeDefined();
    await wrapper.get('.lab-composer').trigger('submit');
    expect(api.transport.mock.calls.some(([path]) => path.endsWith('/turns'))).toBe(false);
  });
  it('cancels an explicit recovery backoff before any new provider connection', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] }); vi.spyOn(Math, 'random').mockReturnValue(0);
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    api.voiceObserver!.phase('error'); api.voiceObserver!.error('connection_closed', true); await flushPromises();
    await wrapper.findAll('button').find(button => button.text().startsWith('Zkusit hlas znovu'))!.trigger('click');
    expect(api.voiceConnect).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(400); expect(api.voiceConnect).not.toHaveBeenCalled();
    await wrapper.findAll('button').find(button => button.text() === 'Zrušit obnovu')!.trigger('click');
    await vi.advanceTimersByTimeAsync(1000); expect(api.voiceConnect).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain('Hlas je vypnutý');
  });
  it('allows at most two explicit transient recovery attempts and explains the new context', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] }); vi.spyOn(Math, 'random').mockReturnValue(0);
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    for (let attempt = 1; attempt <= 2; attempt++) {
      api.voiceObserver!.phase('error'); api.voiceObserver!.error('connection_closed', true); await flushPromises();
      await wrapper.findAll('button').find(button => button.text().startsWith('Zkusit hlas znovu'))!.trigger('click');
      await vi.advanceTimersByTimeAsync(attempt === 1 ? 500 : 1000); await flushPromises();
      expect(api.voiceConnect).toHaveBeenCalledTimes(attempt);
    }
    api.voiceObserver!.phase('error'); api.voiceObserver!.error('connection_closed', true); await flushPromises();
    expect(wrapper.findAll('button').some(button => button.text().startsWith('Zkusit hlas znovu'))).toBe(false);
    expect(wrapper.text()).toContain('Nové hlasové spojení vytváří nový modelový kontext');
  });
  it('cancels recovery on offline and never restarts automatically when online returns', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] }); vi.spyOn(Math, 'random').mockReturnValue(0);
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    api.voiceObserver!.phase('error'); api.voiceObserver!.error('connection_closed', true); await flushPromises();
    await wrapper.findAll('button').find(button => button.text().startsWith('Zkusit hlas znovu'))!.trigger('click');
    const network = vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false); window.dispatchEvent(new Event('offline'));
    await vi.advanceTimersByTimeAsync(2000); expect(api.voiceConnect).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain('Připojení k síti není dostupné');
    network.mockReturnValue(true); window.dispatchEvent(new Event('online')); await vi.advanceTimersByTimeAsync(2000);
    expect(api.voiceConnect).not.toHaveBeenCalled(); expect(wrapper.text()).toContain('Hlas spustíte výslovně');
  });
  it('rejects an auth retry hint but offers bounded recovery for a reported provider capacity failure', async () => {
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    api.voiceObserver!.phase('error'); api.voiceObserver!.error('lab_decision_provider_auth', true); await flushPromises();
    expect(wrapper.findAll('button').some(button => button.text().startsWith('Zkusit hlas znovu'))).toBe(false);
    expect(wrapper.text()).toContain('serverové oprávnění');
    api.voiceObserver!.error('lab_provider_capacity', true); await flushPromises();
    expect(wrapper.findAll('button').some(button => button.text().startsWith('Zkusit hlas znovu'))).toBe(true);
    expect(wrapper.text()).toContain('kapacitní omezení');
  });
  it('offers account reauthentication only for an account-auth failure, retaining microphone restart and text', async () => {
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    api.voiceObserver!.phase('error'); api.voiceObserver!.error('account_session_revoked', false); await flushPromises();
    const reauthLinks = () => wrapper.findAll('a').filter(link => link.text() === 'Ověřit přihlášení');
    expect(reauthLinks()).toHaveLength(1);
    api.voiceObserver!.error('microphone_permission', false); await flushPromises();
    expect(reauthLinks()).toHaveLength(0);
    expect(wrapper.findAll('button').some(button => button.text() === 'Spustit hlas')).toBe(true);
    expect(wrapper.get('#lab-message').attributes('disabled')).toBeUndefined();
    api.voiceObserver!.error('lab_decision_provider_auth', false); await flushPromises();
    expect(reauthLinks()).toHaveLength(0);
  });
  it('filters by client-reported execution platform, not preparation platform, and keeps token gaps distinct', async () => {
    session.events = [{ id: 'web-tokens', type: 'provider_tokens', code: 'observed', createdAt: session.createdAt, inputTokens: 0, outputTokens: null }];
    const baseline = api.transport.getMockImplementation()!;
    const ios = { ...session, id: 'ios-test', platform: 'web', executionPlatforms: ['ios'], events: [{ ...session.events[0], id: 'ios-tokens', inputTokens: 7, outputTokens: 2 }] };
    const mixed = { ...session, id: 'mixed-test', executionPlatforms: ['web', 'ios'] };
    const legacy = { ...session, id: 'legacy-test', platform: 'ios', executionPlatforms: undefined };
    api.transport.mockImplementation((path: string, init: RequestInit) => path.endsWith('/sessions') && init.method === 'GET'
      ? Promise.resolve(new Response(JSON.stringify([session, ios, mixed, legacy]))) : baseline(path, init));
    const wrapper = await render();
    await wrapper.get('#lab-filter-platform').setValue('web');
    const panel = wrapper.get('[aria-labelledby="lab-token-title"]');
    expect(panel.findAll('dd')[0].text()).toContain('0 · 1 pozorování z 1 / 1 sezení');
    expect(panel.findAll('dd')[1].text()).toContain('Neuvedeno');
    await wrapper.get('#lab-filter-platform').setValue('ios');
    expect(panel.findAll('dd')[0].text()).toContain('7 · 1 pozorování z 1 / 1 sezení');
    expect(panel.findAll('dd')[1].text()).toContain('2 · 1 pozorování z 1 / 1 sezení');
    await wrapper.get('#lab-filter-platform').setValue('mixed');
    expect(panel.findAll('dd')[0].text()).toContain('0 · 1 pozorování z 1 / 1 sezení');
    await wrapper.get('#lab-filter-platform').setValue('unknown');
    expect(panel.findAll('dd')[0].text()).toContain('0 · 1 pozorování z 1 / 1 sezení');
    await wrapper.get('#lab-existing').setValue('legacy-test'); await flushPromises();
    expect(wrapper.text()).toContain('Příprava sezení');
    expect(wrapper.text()).toContain('Provedení testu');
    expect(wrapper.text()).toContain('Neuvedeno · hlášení klienta');
  });
  it('keeps a hardware-bound session out of ordinary browser text, voice and image input', async () => {
    session.hardware = { deviceId: 'old-case', bootId: 'old-boot', expectedRevision: 1, conversationId: null,
      status: 'ended', expiresAt: '2026-10-20T13:00:00Z' };
    const wrapper = await render(); await wrapper.get('#lab-existing').setValue('session');
    expect(wrapper.text()).toContain('SKUTEČNÝ KUFR');
    expect(wrapper.get('#lab-message').attributes('disabled')).toBeDefined();
    expect(wrapper.findAll('button').some(button => button.text() === 'Spustit hlas')).toBe(false);
    await wrapper.get('.lab-composer').trigger('submit'); await flushPromises();
    expect(api.transport.mock.calls.some(([path]) => path.endsWith('/turns'))).toBe(false);
  });
});
