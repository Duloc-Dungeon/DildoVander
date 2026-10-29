import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
const api = vi.hoisted(() => ({ request: vi.fn(), transport: vi.fn(), connect: vi.fn(), dispose: vi.fn(), interrupt: vi.fn(), play: vi.fn(), observer: null as any }));
vi.mock('../identity/session', async () => { const { ref } = await import('vue'); return { account: ref({ id: 'alice', openAiConsent: true }), accountOrigin: ref('https://example.test'), accountRequest: api.request, accountTransport: api.transport }; });
vi.mock('../persona-lab/voice', () => ({ LabVoice: class { constructor(observer: unknown) { api.observer = observer; } connect = api.connect; dispose = api.dispose; interrupt = api.interrupt; setMuted = vi.fn(); end() { api.observer.phase('ended'); } } }));
vi.mock('./workshopVoice', () => ({ WorkshopVoice: class { constructor(private observer: { phase(value: string): void }) {} unlock = vi.fn(); async play() { api.play(); this.observer.phase('speaking'); } stop() { this.observer.phase('off'); } } }));
import WandWorkshop from './WandWorkshop.vue';
import { account } from '../identity/session';
const base = '/app/v1/customer/wand/workshop'; const id = 'b'.repeat(32);
let session: any; let reason: string | null; let view: VueWrapper | undefined; let profileRevision: number;
const saved = () => ({ id, profileId: 'profile', profileRevision, tier: 1, status: 'asking', question: null, steps: [], messages: [], wand: null, artwork: null });
const completed = () => ({
  ...saved(), status: 'complete', verdict: 'Tvoje cesta má konečně doprovod.',
  wand: { wandId: 'f'.repeat(32), name: 'Modrá nit', material: 'Jasan', core: 'Jiskra', temperament: 'Klidná', lore: 'Příběh', designDescription: 'Modrá nit.', affinity: 'Mosty', weakness: 'Spěch', combatArchetype: 'balanced', combatRulesVersion: 'wand-archetypes-v1', spells: ['strike', 'trick', 'shield'].map(action => ({ action, name: action, description: 'Kouzlo' })) },
});
const render = async (prelaunch = false) => { view = mount(WandWorkshop, { props: { prelaunch }, global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } }); await flushPromises(); return view; };
const click = async (text: string) => { await view!.findAll('button').find(button => button.text() === text)!.trigger('click'); await flushPromises(); };
beforeEach(() => {
  account.value = { ...account.value!, id: 'alice', openAiConsent: true };
  vi.clearAllMocks(); reason = null; session = null; profileRevision = 1;
  api.request.mockImplementation(async (path: string, method = 'GET', body?: any) => {
    if (path === base) return { eligible: !reason, reason, tier: 0, profile: { profileId: 'profile', revision: profileRevision, name: 'Sever', world: 'Přístav', status: reason ? 'running' : 'ready' }, session: structuredClone(session) };
    if (path === `${base}/sessions`) { session ??= { ...saved(), id: body.operationId }; return { session: structuredClone(session) }; }
    if (path.endsWith('/ticket')) return { url: 'wss://example.test/live', path: '/live', expiresAt: '' };
    if (path.endsWith('/messages')) { session.messages.push({ messageId: body.messageId, revision: 1, role: 'user', text: body.text }); session.messages.push({ messageId: `reply${session.messages.length}`, revision: 1, role: 'assistant', text: 'Co má hůlka zachránit?' }); }
    if (path.endsWith('/finish')) session.status = 'generating';
    return { session: structuredClone(session) };
  });
  api.connect.mockImplementation(async (ticket: () => Promise<unknown>) => { api.observer.phase('connecting'); await ticket(); api.observer.phase('listening'); });
});
afterEach(() => { view?.unmount(); view = undefined; vi.useRealTimers(); });
describe('customer Live workshop', () => {
  it('acknowledges completion observed in this workshop and keeps a restored result quiet', async () => {
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    session = { ...saved(), status: 'generating' };
    await render();
    expect(view!.attributes('data-new-wand')).toBe('false');
    api.observer.workshopSession(completed());
    await flushPromises();
    expect(view!.attributes('data-new-wand')).toBe('true');
    expect(view!.get('.trunk-workshop__saved').attributes('role')).toBe('status');
    expect(view!.get('.trunk-workshop__saved').text()).toBe('Hůlka je uložená a připravená ke hře.');
    view!.unmount();
    session = completed();
    await render();
    expect(view!.attributes('data-new-wand')).toBe('false');
  });
  it('does not celebrate a completion received while the document is hidden', async () => {
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(true);
    session = { ...saved(), status: 'generating' };
    await render();
    api.observer.workshopSession(completed());
    await flushPromises();
    expect(view!.attributes('data-new-wand')).toBe('false');
    expect(view!.text()).toContain('Hůlka je uložená a připravená ke hře.');
  });
  it('returns prelaunch guests to their countdown and stops media without linking to the game', async () => {
    await render(true);
    expect(view!.find('a').exists()).toBe(false);
    await click('Promluvit s kufrem');
    await click('Zpět k odpočtu');
    expect(view!.emitted('exit')).toEqual([[]]);
    expect(api.dispose).toHaveBeenCalled();
    expect(view!.emitted('saved')).toBeUndefined();
  });
  it('only reads on entry and offers Live first plus typing without acquiring audio', async () => {
    await render(); expect(api.request.mock.calls.map(call => call[0])).toEqual([base]); expect(api.connect).not.toHaveBeenCalled(); expect(view!.text()).toContain('Promluvit s kufrem'); expect(view!.text()).toContain('Raději psát'); expect(view!.text()).not.toMatch(/Otázka .* ze 3/);
  });
  it('guides text answers without reporting an unused microphone or starting Live', async () => {
    await render(true);
    await click('Raději psát');
    expect(view!.get('textarea').attributes('aria-describedby')).toBe('trunk-answer-hint');
    expect(view!.get('#trunk-answer-hint').text()).toContain('Stačí jedna věta');
    expect(view!.text()).not.toContain('Mikrofon je vypnutý.');
    expect(view!.text()).toContain('Hůlka vznikne z uložených odpovědí.');
    expect(api.connect).not.toHaveBeenCalled();
    expect(view!.findAll('button').find(button => button.text() === 'Odeslat')!.attributes('disabled')).toBeDefined();
  });
  it('keeps one saved result and one recoverable illustration error without an empty art area', async () => {
    session = { ...completed(), artwork: { status: 'failed', assetId: null, canRetry: true, version: 1 } };
    await render(true);
    expect(view!.get('#workshop-title').text()).toBe('Modrá nit');
    expect(view!.text().match(/Hůlka je uložená/g)).toHaveLength(1);
    expect(view!.text().match(/Ilustrace se nezdařila/g)).toHaveLength(1);
    expect(view!.get('.trunk-stage').attributes('style')).toContain('display: none');
    await click('Počkat na začátek večera');
    expect(view!.emitted('exit')).toEqual([[]]);
    expect(api.request.mock.calls.some(call => call[1] === 'POST')).toBe(false);
  });
  it.each(['wand_character_preparing', 'wand_character_needs_clarification', 'wand_character_preparation_failed'])('blocks both modes while %s', async value => {
    reason = value; await render(); expect(view!.findAll('button').some(button => ['Promluvit s kufrem', 'Raději psát'].includes(button.text()))).toBe(false); expect(api.connect).not.toHaveBeenCalled();
  });
  it('announces confirmed text replies without streaming fragments', async () => {
    await render(); await click('Raději psát');
    const announcement = view!.get('[aria-live="polite"]');
    expect(announcement.text()).toBe('Co má hůlka zachránit?');
    api.observer.transcript('assistant', 'Nedokončená věta'); await flushPromises();
    expect(view!.get('.trunk-workshop__question').text()).toBe('Nedokončená věta');
    expect(announcement.text()).toBe('Co má hůlka zachránit?');
  });
  it('prioritizes confirmed manufacture over speech and keeps failed work recoverable', async () => {
    session = saved(); await render(); await click('Pokračovat hlasem');
    api.observer.phase('speaking'); api.observer.outputLevel(.8); await flushPromises();
    expect(view!.get('.trunk-stage').attributes('data-stage')).toBe('speaking');
    api.observer.workshopSession({ ...saved(), status: 'generating' }); await flushPromises();
    expect(view!.get('.trunk-stage').attributes('data-stage')).toBe('forging');
    expect(view!.get('[aria-current="step"]').text()).toBe('Výroba');
    expect(view!.get('.trunk-stage__visual').attributes('style')).toContain('--trunk-level: 0;');
    api.observer.workshopSession({ ...saved(), status: 'failed', canRetry: true }); await flushPromises();
    expect(view!.get('.trunk-stage').attributes('data-stage')).toBe('error');
    expect(view!.text()).toContain('Obnovit výrobu');
    expect(view!.text()).not.toContain('Pokračovat hlasem');
  });
  it('explicitly connects customer workshop Live with its own ticket', async () => {
    await render(); await click('Promluvit s kufrem'); expect(api.connect).toHaveBeenCalledOnce(); expect(api.connect.mock.calls[0][3]).toEqual({ purpose: 'workshop' }); expect(api.request.mock.calls.some(call => call[0] === `${base}/sessions/${session.id}/ticket`)).toBe(true); expect(api.request.mock.calls.some(call => String(call[0]).includes('persona-lab'))).toBe(false);
  });
  it('supports four exchanges before a separate finish event', async () => {
    await render(); await click('Raději psát'); expect(api.connect).not.toHaveBeenCalled();
    for (let i = 0; i < 4; i++) { await view!.get('textarea').setValue(i === 1 ? 'Nevím' : `Podklad ${i}`); await view!.get('form').trigger('submit'); await flushPromises(); expect(session.status).toBe('asking'); }
    await click('To stačí, vyrob hůlku'); expect(session.status).toBe('generating'); expect(api.request.mock.calls.filter(call => String(call[0]).endsWith('/finish'))).toHaveLength(1);
  });
  it('keeps one operation through voice, text and voice', async () => {
    session = saved(); session.messages.push({ messageId: 'old', revision: 1, role: 'user', text: 'Zachraň most.' }); await render(); await click('Pokračovat hlasem'); await click('Raději psát'); expect(view!.text()).toContain('Zachraň most.'); await click('Pokračovat hlasem'); expect(api.request.mock.calls.filter(call => call[0] === `${base}/sessions`)).toHaveLength(0); expect(api.dispose).toHaveBeenCalled();
  });
  it('supports Stop for real playback and releases audio on leaving', async () => {
    await render(); await click('Promluvit s kufrem'); api.observer.phase('speaking'); await flushPromises(); await click('Stop řeč'); expect(api.interrupt).toHaveBeenCalledOnce(); view!.unmount(); view = undefined; expect(api.dispose).toHaveBeenCalled();
  });
  it('keeps Live running through a failed poll and clears the sync message after recovery', async () => {
    vi.useFakeTimers();
    await render(); await click('Promluvit s kufrem');
    api.observer.phase('speaking'); await flushPromises();
    const disposed = api.dispose.mock.calls.length;
    api.request.mockRejectedValueOnce(Object.assign(new Error('Limited'), { status: 429 }));
    await vi.advanceTimersByTimeAsync(3000); await flushPromises();
    expect(view!.text()).toContain('Hlasové spojení pokračuje');
    expect(view!.text()).toContain('Ztlumit mikrofon');
    expect(api.dispose).toHaveBeenCalledTimes(disposed);
    expect(view!.find('textarea').exists()).toBe(false);
    const reads = api.request.mock.calls.length;
    await vi.advanceTimersByTimeAsync(5999); expect(api.request).toHaveBeenCalledTimes(reads);
    await vi.advanceTimersByTimeAsync(1); await flushPromises();
    expect(view!.text()).not.toContain('Aktualizace stavu se opozdila');
    expect(api.connect).toHaveBeenCalledOnce();
  });
  it('coalesces manual refresh while a status request is still pending', async () => {
    vi.useFakeTimers(); await render();
    api.request.mockRejectedValueOnce(new Error('Offline'));
    await vi.advanceTimersByTimeAsync(3000); await flushPromises();
    let resolve!: (value: unknown) => void;
    api.request.mockImplementationOnce(() => new Promise(done => { resolve = done; }));
    await click('Obnovit stav'); await click('Obnovit stav');
    const reads = api.request.mock.calls.length;
    await vi.advanceTimersByTimeAsync(30000);
    expect(api.request).toHaveBeenCalledTimes(reads);
    resolve({ eligible: true, reason: null, tier: 0, profile: { profileId: 'profile', revision: 1, name: 'Sever', world: 'Přístav' }, session: null });
    await flushPromises(); expect(view!.text()).not.toContain('Stav se nepodařilo načíst');
  });
  it('bounds a hung status request and recovers without ending Live', async () => {
    vi.useFakeTimers(); await render(); await click('Promluvit s kufrem');
    const disposed = api.dispose.mock.calls.length;
    let pollSignal!: AbortSignal;
    api.request.mockImplementationOnce((_path: string, _method: string, _body: unknown, signal: AbortSignal) => new Promise((_resolve, reject) => {
      pollSignal = signal;
      signal.addEventListener('abort', () => reject(new DOMException('Timed out', 'AbortError')), { once: true });
    }));
    await vi.advanceTimersByTimeAsync(3000);
    await vi.advanceTimersByTimeAsync(15000); await flushPromises();
    expect(pollSignal.aborted).toBe(true);
    expect(view!.text()).toContain('Hlasové spojení pokračuje');
    await click('Obnovit stav');
    expect(view!.text()).not.toContain('Aktualizace stavu se opozdila');
    expect(api.dispose).toHaveBeenCalledTimes(disposed);
    expect(api.connect).toHaveBeenCalledOnce();
  });
  it('does not replace a newer Live manufacturing state with a delayed HTTP snapshot', async () => {
    vi.useFakeTimers(); await render(); await click('Promluvit s kufrem');
    let resolve!: (value: unknown) => void;
    api.request.mockImplementationOnce(() => new Promise(done => { resolve = done; }));
    await vi.advanceTimersByTimeAsync(3000);
    api.observer.workshopSession({ ...structuredClone(session), status: 'generating' }); await flushPromises();
    resolve({ eligible: true, reason: null, tier: 0, profile: { profileId: 'profile', revision: 1, name: 'Sever', world: 'Přístav' }, session: structuredClone(session) });
    await flushPromises();
    expect(view!.text()).toContain('Hůlka dostává tvar');
    expect(view!.text()).not.toContain('Pokračovat hlasem');
  });
  it('keeps Live and its subtitle when the same account is successfully reconciled', async () => {
    await render(); await click('Promluvit s kufrem');
    api.observer.transcript('assistant', 'Ještě tě poslouchám.'); await flushPromises();
    const disposed = api.dispose.mock.calls.length; const reads = api.request.mock.calls.length;
    account.value = { ...account.value!, displayName: 'Aktualizované jméno' };
    await flushPromises();
    expect(api.dispose).toHaveBeenCalledTimes(disposed);
    expect(api.request).toHaveBeenCalledTimes(reads);
    expect(view!.text()).toContain('Ještě tě poslouchám.');
    expect(view!.text()).toContain('Ztlumit mikrofon');
    expect(api.connect).toHaveBeenCalledOnce();
  });
  it.each(['owner', 'consent'])('releases Live and clears captions when %s actually changes', async changed => {
    await render(); await click('Promluvit s kufrem');
    api.observer.transcript('assistant', 'Soukromá předchozí replika.'); await flushPromises();
    const disposed = api.dispose.mock.calls.length;
    session = null;
    account.value = changed === 'owner' ? { ...account.value!, id: 'bob' } : { ...account.value!, openAiConsent: false };
    await flushPromises();
    expect(api.dispose.mock.calls.length).toBeGreaterThan(disposed);
    expect(view!.text()).not.toContain('Soukromá předchozí replika.');
    expect(view!.text()).not.toContain('Ztlumit mikrofon');
    expect(api.connect).toHaveBeenCalledOnce();
  });
  it.each(['voice', 'text'])('restarts %s with a fresh operation after the profile revision changes during a poll', async nextMode => {
    vi.useFakeTimers(); await render(); await click('Promluvit s kufrem');
    const previous = structuredClone(session); const disposed = api.dispose.mock.calls.length;
    let resolve!: (value: unknown) => void;
    api.request.mockImplementationOnce(() => new Promise(done => { resolve = done; }));
    await vi.advanceTimersByTimeAsync(3000);
    // A late state event must not defeat a confirmed profile-context change.
    api.observer.workshopSession({ ...previous, messages: [{ messageId: 'old', role: 'assistant', text: 'Stará otázka.' }] });
    api.observer.transcript('assistant', 'Stará replika.'); await flushPromises();
    profileRevision = 2; session = null;
    resolve({ eligible: true, reason: null, tier: 0, profile: { profileId: 'profile', revision: 2, name: 'Sever', world: 'Přístav' }, session: null });
    await flushPromises();
    expect(api.dispose.mock.calls.length).toBeGreaterThan(disposed);
    expect(view!.text()).not.toContain('Stará replika.');
    expect(view!.text()).not.toContain('Stará otázka.');
    expect(api.connect).toHaveBeenCalledOnce();
    await click(nextMode === 'voice' ? 'Promluvit s kufrem' : 'Raději psát');
    const starts = api.request.mock.calls.filter(call => call[0] === `${base}/sessions`);
    expect(starts).toHaveLength(2);
    expect(starts[1][2].operationId).not.toBe(previous.id);
    expect(session.profileRevision).toBe(2);
    expect(api.connect).toHaveBeenCalledTimes(nextMode === 'voice' ? 2 : 1);
  });
  it('keeps a new preparing revision blocked and starts only after its preparation completes', async () => {
    vi.useFakeTimers(); await render(); await click('Promluvit s kufrem');
    const previousId = session.id;
    profileRevision = 2; reason = 'wand_character_preparing'; session = null;
    await vi.advanceTimersByTimeAsync(3000); await flushPromises();
    expect(view!.text()).toContain('Ještě připravujeme tvoji postavu.');
    expect(view!.findAll('button').some(button => ['Promluvit s kufrem', 'Raději psát'].includes(button.text()))).toBe(false);
    expect(api.request.mock.calls.filter(call => call[0] === `${base}/sessions`)).toHaveLength(1);
    reason = null;
    await vi.advanceTimersByTimeAsync(2000); await flushPromises();
    expect(api.connect).toHaveBeenCalledOnce();
    await click('Raději psát');
    expect(session.id).not.toBe(previousId); expect(session.profileRevision).toBe(2);
    expect(api.connect).toHaveBeenCalledOnce();
  });
  it('cancels a pending final verdict and cannot replay its late audio response', async () => {
    session = completed();
    let deliver!: (response: Response) => void;
    api.transport.mockImplementation(() => new Promise<Response>(resolve => { deliver = resolve; }));
    await render(); await click('Přehrát verdikt kufru');
    expect(view!.emitted('saved')).toEqual([[]]);
    expect(view!.findAll('button').find(button => button.text() === 'Přehrát verdikt kufru')!.attributes('disabled')).toBeDefined();
    await click('Stop řeč'); deliver(new Response(new Blob(['late audio'], { type: 'audio/wav' }))); await flushPromises();
    expect(api.play).not.toHaveBeenCalled(); expect(view!.text()).toContain('Hůlka je uložená');
  });
});
