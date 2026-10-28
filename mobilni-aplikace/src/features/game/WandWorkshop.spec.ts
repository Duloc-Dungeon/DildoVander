import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
const api = vi.hoisted(() => ({ request: vi.fn(), transport: vi.fn(), connect: vi.fn(), dispose: vi.fn(), interrupt: vi.fn(), play: vi.fn(), observer: null as any }));
vi.mock('../identity/session', async () => { const { ref } = await import('vue'); return { account: ref({ id: 'alice', openAiConsent: true }), accountOrigin: ref('https://example.test'), accountRequest: api.request, accountTransport: api.transport }; });
vi.mock('../persona-lab/voice', () => ({ LabVoice: class { constructor(observer: unknown) { api.observer = observer; } connect = api.connect; dispose = api.dispose; interrupt = api.interrupt; setMuted = vi.fn(); end = vi.fn(); } }));
vi.mock('./workshopVoice', () => ({ WorkshopVoice: class { constructor(private observer: { phase(value: string): void }) {} unlock = vi.fn(); async play() { api.play(); this.observer.phase('speaking'); } stop() { this.observer.phase('off'); } } }));
import WandWorkshop from './WandWorkshop.vue';
const base = '/app/v1/customer/wand/workshop'; const id = 'b'.repeat(32);
let session: any; let reason: string | null; let view: VueWrapper | undefined;
const saved = () => ({ id, profileId: 'profile', profileRevision: 1, tier: 1, status: 'asking', question: null, steps: [], messages: [], wand: null, artwork: null });
const render = async () => { view = mount(WandWorkshop, { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } }); await flushPromises(); return view; };
const click = async (text: string) => { await view!.findAll('button').find(button => button.text() === text)!.trigger('click'); await flushPromises(); };
beforeEach(() => {
  vi.clearAllMocks(); reason = null; session = null;
  api.request.mockImplementation(async (path: string, method = 'GET', body?: any) => {
    if (path === base) return { eligible: !reason, reason, tier: 0, profile: { profileId: 'profile', revision: 1, name: 'Sever', world: 'Přístav', status: reason ? 'running' : 'ready' }, session: structuredClone(session) };
    if (path === `${base}/sessions`) { session ??= saved(); return { session: structuredClone(session) }; }
    if (path.endsWith('/ticket')) return { url: 'wss://example.test/live', path: '/live', expiresAt: '' };
    if (path.endsWith('/messages')) { session.messages.push({ messageId: body.messageId, revision: 1, role: 'user', text: body.text }); session.messages.push({ messageId: `reply${session.messages.length}`, revision: 1, role: 'assistant', text: 'Co má hůlka zachránit?' }); }
    if (path.endsWith('/finish')) session.status = 'generating';
    return { session: structuredClone(session) };
  });
  api.connect.mockImplementation(async (ticket: () => Promise<unknown>) => { api.observer.phase('connecting'); await ticket(); api.observer.phase('listening'); });
});
afterEach(() => { view?.unmount(); view = undefined; });
describe('customer Live workshop', () => {
  it('only reads on entry and offers Live first plus typing without acquiring audio', async () => {
    await render(); expect(api.request.mock.calls.map(call => call[0])).toEqual([base]); expect(api.connect).not.toHaveBeenCalled(); expect(view!.text()).toContain('Promluvit s kufrem'); expect(view!.text()).toContain('Raději psát'); expect(view!.text()).not.toMatch(/Otázka .* ze 3/);
  });
  it.each(['wand_character_preparing', 'wand_character_needs_clarification', 'wand_character_preparation_failed'])('blocks both modes while %s', async value => {
    reason = value; await render(); expect(view!.findAll('button').some(button => ['Promluvit s kufrem', 'Raději psát'].includes(button.text()))).toBe(false); expect(api.connect).not.toHaveBeenCalled();
  });
  it('explicitly connects customer workshop Live with its own ticket', async () => {
    await render(); await click('Promluvit s kufrem'); expect(api.connect).toHaveBeenCalledOnce(); expect(api.connect.mock.calls[0][3]).toEqual({ purpose: 'workshop' }); expect(api.request.mock.calls.some(call => call[0] === `${base}/sessions/${id}/ticket`)).toBe(true); expect(api.request.mock.calls.some(call => String(call[0]).includes('persona-lab'))).toBe(false);
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
  it('cancels a pending final verdict and cannot replay its late audio response', async () => {
    session = { ...saved(), status: 'complete', verdict: 'Uložená hůlka.', wand: { wandId: 'f'.repeat(32), name: 'Modrá nit', material: 'Jasan', core: 'Jiskra', temperament: 'Klidná', lore: 'Příběh', designDescription: 'Modrá nit.', affinity: 'Mosty', weakness: 'Spěch', combatArchetype: 'balanced', combatRulesVersion: 'wand-archetypes-v1', spells: ['strike', 'trick', 'shield'].map(action => ({ action, name: action, description: 'Kouzlo' })) } };
    let deliver!: (response: Response) => void;
    api.transport.mockImplementation(() => new Promise<Response>(resolve => { deliver = resolve; }));
    await render(); await click('Přehrát verdikt kufru');
    expect(view!.findAll('button').find(button => button.text() === 'Přehrát verdikt kufru')!.attributes('disabled')).toBeDefined();
    await click('Stop řeč'); deliver(new Response(new Blob(['late audio'], { type: 'audio/wav' }))); await flushPromises();
    expect(api.play).not.toHaveBeenCalled(); expect(view!.text()).toContain('Hůlka je uložená');
  });
});
