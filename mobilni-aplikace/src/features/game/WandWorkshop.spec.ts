import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
const api = vi.hoisted(() => ({ request: vi.fn(), transport: vi.fn(), record: vi.fn(), stop: vi.fn(), dispose: vi.fn(), play: vi.fn() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'alice', openAiConsent: true }), accountRequest: api.request, accountTransport: api.transport };
});
vi.mock('./workshopVoice', () => ({ WorkshopVoice: class {
  constructor(private observer: { phase(value: string): void }) {}
  unlock = vi.fn(); play = api.play; finishRecording = vi.fn();
  async record() { const result = await api.record(); this.observer.phase('off'); return result; }
  stop() { api.stop(); this.observer.phase('off'); } dispose() { api.dispose(); }
} }));
import WandWorkshop from './WandWorkshop.vue';
const base = '/app/v1/customer/wand/workshop';
const id = 'b'.repeat(32);
const questions = ['Co uděláš, když se před tebou zavře cesta?', 'Co má hůlka udělat, když tvoje první řešení selže?', 'Jakou drobnou protivnost své hůlce odpustíš?'];
const question = (number: number) => ({ questionId: String(number).repeat(32), questionRevision: 1, number, question: questions[number - 1], basedOnFactIds: [] });
let session: Record<string, any> | null;
let eligible: boolean;
let view: VueWrapper | undefined;
const entry = () => ({ eligible, reason: eligible ? null : 'wand_workshop_boss_captured', tier: 0,
  profile: { profileId: 'profile', revision: 1, name: 'Sever', world: 'Půlnoční přístav', version: '', description: '' }, session });
const saved = () => ({ id, profileId: 'profile', profileRevision: 1, tier: 1, status: 'asking', question: question(1), steps: [], wand: null, artwork: null });
const closedSteps = () => [1, 2, 3].map(number => ({ ...question(number), answerId: String(number + 3).repeat(32), answerRevision: 1, answer: 'Nevím', skipped: false, sourceMode: 'text' }));
const render = async () => {
  view = mount(WandWorkshop, { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } });
  await flushPromises(); return view;
};
const click = async (text: string) => { await view!.findAll('button').find(button => button.text() === text)!.trigger('click'); await flushPromises(); };
const answer = async (text: string) => { await view!.get('textarea').setValue(text); await view!.get('form').trigger('submit'); await flushPromises(); };
beforeEach(() => {
  vi.clearAllMocks(); session = null; eligible = true;
  api.request.mockImplementation(async (path: string, method = 'GET', body?: any) => {
    if (path === base) return structuredClone(entry());
    if (path === `${base}/sessions`) { session ??= saved(); return { session: structuredClone(session) }; }
    if (path.endsWith('/retry')) { session!.status = 'generating'; return { session: structuredClone(session) }; }
    if (method === 'GET') return { session: structuredClone(session) };
    if (method === 'PUT') { const step = session!.steps.find((item: any) => path.endsWith(item.answerId)); step.answer = body.answer; step.answerRevision++; return { session: structuredClone(session) }; }
    const active = session!.question;
    session!.steps.push({ ...active, answerId: String(active.number + 3).repeat(32), answerRevision: 1, answer: body.answer ?? null, skipped: path.endsWith('/skip'), sourceMode: body.sourceMode ?? 'text' });
    session!.question = active.number < 3 ? question(active.number + 1) : null;
    if (active.number === 3) session!.status = 'generating';
    return { session: structuredClone(session) };
  });
  api.record.mockResolvedValue(new Blob(['audio'], { type: 'audio/webm' }));
  api.transport.mockResolvedValue(Response.json({ text: 'Schovám se v mlze.' }));
});
afterEach(() => { view?.unmount(); view = undefined; vi.restoreAllMocks(); });

describe('controlled customer wand workshop', () => {
  it('only reads on entry; no microphone, paid operation or staff endpoint', async () => {
    await render(); expect(api.request.mock.calls.map(call => call[0])).toEqual([base]);
    expect(api.record).not.toHaveBeenCalled(); expect(api.transport).not.toHaveBeenCalled();
    expect(view!.text()).toContain('Sever'); expect(view!.text()).toContain('Půlnoční přístav');
    expect(view!.find('img').attributes('src')).toContain('trunk-closed');
  });
  it('uses exactly the three server questions with no pre-generation or fourth question', async () => {
    await render(); await click('Otevřít dílnu');
    expect(view!.text()).toContain('Otázka 1 ze 3'); expect(view!.get('img').attributes('src')).toContain('trunk-open');
    await answer('Zkusím jinou cestu.'); expect(view!.text()).toContain('Otázka 2 ze 3');
    await answer('Nevím'); expect(session!.status).toBe('asking'); expect(view!.text()).toContain('Otázka 3 ze 3');
    await click('Přeskočit tuto otázku');
    expect(session!.steps).toHaveLength(3); expect(session!.status).toBe('generating');
    expect(view!.find('textarea').exists()).toBe(false); expect(view!.text()).not.toContain('Otázka 4');
    expect(api.request.mock.calls.filter(call => call[1] === 'POST')).toHaveLength(4);
  });
  it('resumes the same question on remount and corrections preserve the count', async () => {
    session = { ...saved(), question: question(2), steps: [{ ...question(1), answerId: 'c'.repeat(32), answerRevision: 1, answer: 'Půjdu přímo.', skipped: false, sourceMode: 'text' }] };
    await render(); expect(view!.text()).toContain('Otázka 2 ze 3');
    await click('Opravit odpověď 1'); await view!.get('#trunk-correction').setValue('Změním směr.'); await view!.get('#trunk-correction').element.closest('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); await flushPromises();
    expect(session.steps).toHaveLength(1); expect(session.steps[0].answerRevision).toBe(2);
    view!.unmount(); view = undefined; await render();
    expect(view!.text()).toContain('Změním směr.'); expect(view!.text()).toContain('Otázka 2 ze 3');
    expect(api.request.mock.calls.some(call => call[0] === `${base}/sessions`)).toBe(false);
  });
  it('transcribes a voice draft into the same session and only advances on submit', async () => {
    await render(); await click('Otevřít dílnu'); await click('Odpovědět hlasem');
    await click('Zapnout mikrofon a nahrát odpověď');
    expect(api.transport.mock.calls[0][0]).toBe(`${base}/sessions/${id}/transcribe`);
    expect(api.transport.mock.calls[0][1].headers).toMatchObject({ 'X-Wand-Question-Id': '1'.repeat(32), 'X-Wand-Question-Revision': '1' });
    expect(session!.steps).toHaveLength(0); expect((view!.get('textarea').element as HTMLTextAreaElement).value).toBe('Schovám se v mlze.');
    await view!.get('form').trigger('submit'); await flushPromises();
    expect(session!.steps[0].sourceMode).toBe('voice'); expect(view!.text()).toContain('Otázka 2 ze 3');
    await click('Odpovědět psaním'); expect(session!.steps).toHaveLength(1);
  });
  it('denied microphone leaves the active question and text input usable', async () => {
    api.record.mockRejectedValue(new DOMException('denied', 'NotAllowedError'));
    await render(); await click('Otevřít dílnu'); await click('Odpovědět hlasem'); await click('Zapnout mikrofon a nahrát odpověď');
    expect(view!.text()).toContain('Pokračuj psaním'); expect(session!.steps).toHaveLength(0);
    await answer('Nevím'); expect(view!.text()).toContain('Otázka 2 ze 3');
  });
  it('blocks manufacture and microphone controls when the server closes the workshop', async () => {
    eligible = false; await render();
    expect(view!.text()).toContain('po jeho porážce'); expect(view!.findAll('button').some(button => button.text() === 'Otevřít dílnu')).toBe(false);
    expect(api.record).not.toHaveBeenCalled();
  });
  it('binds speech to displayed identity and refreshes a stale question before playback', async () => {
    session = saved(); await render();
    session.question = question(2);
    api.transport.mockResolvedValue(new Response('{}', { status: 409 }));
    await click('Přehrát otázku');
    expect(JSON.parse(api.transport.mock.calls[0][1].body)).toEqual({ questionId: '1'.repeat(32), questionRevision: 1 });
    expect(api.play).not.toHaveBeenCalled(); expect(view!.text()).toContain('Otázka 2 ze 3');
  });
  it('ignores an old creation response after navigating to an upgrade', async () => {
    let resolve!: (value: unknown) => void;
    const late = new Promise(complete => { resolve = complete; });
    const original = api.request.getMockImplementation()!;
    api.request.mockImplementation((path: string, method: string, body: unknown, signal: AbortSignal) => {
      if (path === `${base}/sessions` && method === 'POST') return late;
      if (path === `${base}?upgrade=1`) return { ...entry(), session: null, profile: { ...entry().profile, name: 'Nová kapitola' } };
      return original(path, method, body, signal);
    });
    await render(); await click('Otevřít dílnu'); await view!.setProps({ upgrade: true }); await flushPromises();
    resolve({ session: saved() }); await flushPromises();
    expect(view!.text()).toContain('Nová kapitola'); expect(view!.text()).not.toContain('Otázka 1 ze 3');
    expect((api.request.mock.calls.find(call => call[0] === `${base}/sessions`)![3] as AbortSignal).aborted).toBe(true);
  });
  it('checks the existing operation before explicitly resuming a known safe failure', async () => {
    session = { ...saved(), status: 'failed', question: null, canRetry: true, steps: closedSteps() };
    await render(); const before = api.request.mock.calls.length;
    await click('Obnovit tuto výrobu');
    expect(api.request.mock.calls.slice(before).map(call => [call[0], call[1]])).toEqual([
      [`${base}/sessions/${id}`, 'GET'], [`${base}/sessions/${id}/retry`, 'POST'],
    ]);
    expect(session!.status).toBe('generating');
  });
  it('does not offer a new paid attempt for an uncertain dispatch', async () => {
    session = { ...saved(), status: 'failed', question: null, canRetry: false };
    await render(); expect(view!.text()).not.toContain('Obnovit tuto výrobu');
    expect(api.request.mock.calls.every(call => !call[1] || call[1] === 'GET')).toBe(true);
  });
  it('shows authoring as a waiting state without exposing an unapproved question', async () => {
    session = { ...saved(), status: 'authoring', question: null };
    await render();
    expect(view!.text()).toContain('Kufr připravuje otázku'); expect(view!.find('textarea').exists()).toBe(false);
    expect(view!.findAll('button').some(button => button.text() === 'Přehrát otázku')).toBe(false);
    expect(api.transport).not.toHaveBeenCalled(); expect(api.record).not.toHaveBeenCalled();
  });
});
