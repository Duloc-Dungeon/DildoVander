import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent } from 'vue';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
const transport = vi.hoisted(() => vi.fn());
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'tester', roles: ['Tester'] }), accountOrigin: ref(''), accountTransport: transport };
});
vi.mock('../persona-lab/voice', () => ({ LabVoice: class { dispose() {} setOutputEnabled() {} } }));
import { useTestingSessions } from './useTestingSessions';
import { TestingError } from './client';
import type { TestSessionDetail } from './contracts';

let wrapper: VueWrapper | undefined;
let test: ReturnType<typeof useTestingSessions>;
let revision: number;
const detail = () => ({ session: { id: 'test', status: 'open', revision, inputMode: 'text', engine: 'gpt-live-1' }, conversation: { id: 'lab', turns: [] } }) as unknown as TestSessionDetail;
async function render() {
  const host = defineComponent({ setup() { test = useTestingSessions(); return {}; }, template: '<div />' });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/testing-sessions', component: host }] });
  await router.push('/testing-sessions?session=test'); await router.isReady();
  wrapper = mount(host, { global: { plugins: [router] } }); await flushPromises();
}
beforeEach(() => {
  revision = 1;
  transport.mockReset();
  transport.mockImplementation((path: string) => Promise.resolve(Response.json(path.endsWith('/test') ? detail() : [])));
});
afterEach(() => { wrapper?.unmount(); vi.useRealTimers(); });

describe('testing status recovery', () => {
  it('coalesces simultaneous refreshes into one read batch', async () => {
    await render();
    const before = transport.mock.calls.length;
    let release!: (response: Response) => void;
    const healthy = transport.getMockImplementation()!;
    transport.mockImplementation((path: string) => path.endsWith('/test')
      ? new Promise<Response>(resolve => { release = resolve; })
      : healthy(path));
    const first = test.refresh();
    const second = test.refresh();
    expect(transport.mock.calls).toHaveLength(before + 3);
    release(Response.json(detail()));
    await Promise.all([first, second]);
  });
  it('keeps the conversation fresh during collection failure, correlates the error and clears it on recovery', async () => {
    await render(); revision = 2;
    const healthy = transport.getMockImplementation()!;
    transport.mockImplementation((path: string) => path === '/app/v1/testing-sessions'
      ? Promise.resolve(Response.json({ code: 'testing_request_failed', traceId: '01234567-89ab-4cde-8f01-23456789abcd' }, { status: 503 })) : healthy(path));
    await test.refresh();
    expect(test.selected.value?.session.revision).toBe(2);
    expect(test.stale.value).toBe(false);
    expect(test.errorRequestId.value).toBe('01234567-89ab-4cde-8f01-23456789abcd');
    transport.mockImplementation(healthy);
    await test.refresh();
    expect(test.stale.value).toBe(false); expect(test.error.value).toBe(''); expect(test.errorRequestId.value).toBe('');
    expect(transport.mock.calls.every(([, init]) => init.method === 'GET')).toBe(true);
  });
  it('preserves an unconfirmed action after a successful read', async () => {
    await render();
    await test.run(async () => { throw new TestingError('testing_action_failed', 502, '01234567-89ab-4cde-8f01-23456789abcd'); });
    await test.refresh();
    expect(test.error.value).toBe('testing_action_failed');
    expect(test.errorRequestId.value).toBe('01234567-89ab-4cde-8f01-23456789abcd');
  });
  it('fences an older polling snapshot when a mutation starts', async () => {
    await render();
    const old = { ...detail(), conversationActive: true };
    let release!: (response: Response) => void;
    const healthy = transport.getMockImplementation()!;
    transport.mockImplementation((path: string) => path.endsWith('/test') ? new Promise<Response>(resolve => { release = resolve; }) : healthy(path));
    const pending = test.refresh();
    await test.run(async () => { test.selected.value = { ...detail(), conversationActive: false }; });
    release(Response.json(old)); await pending;
    expect(test.selected.value?.session.revision).toBe(1);
    expect(test.selected.value?.conversationActive).toBe(false);
  });
  it('defers visibility reads during a pending mutation and reconciles from a fresh command read', async () => {
    await render();
    let finishCommand!: () => void;
    const command = test.run(async () => {
      await new Promise<void>(resolve => { finishCommand = resolve; });
      revision = 4; test.selected.value = detail();
      await test.refreshAfterCommand();
    });
    const before = transport.mock.calls.length;
    await test.refresh();
    expect(transport.mock.calls).toHaveLength(before);
    finishCommand(); await command;
    expect(transport.mock.calls.length).toBeGreaterThan(before);
    expect(test.selected.value?.session.revision).toBe(4);
  });
  it('clears the previous server trace when a local consent error replaces it', async () => {
    await render();
    await test.run(async () => { throw new TestingError('testing_request_failed', 503, '01234567-89ab-4cde-8f01-23456789abcd'); });
    expect(test.errorRequestId.value).not.toBe('');
    await test.saveCharacter({ name: 'Name', world: 'World' });
    expect(test.error.value).toContain('potvrďte souhlas');
    expect(test.errorRequestId.value).toBe('');
  });
  it('acknowledges a saved character when a subsequent status read fails', async () => {
    await render(); test.consent.value = true;
    const saved = { id: 'saved', profile: { revision: 1, status: 'queued' } };
    transport.mockImplementation((path: string, init: RequestInit) => {
      if (path.endsWith('/characters')) return Promise.resolve(Response.json(init.method === 'POST' ? saved : [saved]));
      if (path.endsWith('/character')) return Promise.resolve(Response.json(detail()));
      return Promise.resolve(Response.json({ code: 'testing_request_failed' }, { status: 503 }));
    });
    expect(await test.saveCharacter({ name: 'Name', world: 'World' })).toBe(true);
    expect(test.characters.value[0].id).toBe('saved');
    expect(test.error.value).toBe('testing_request_failed');
    expect(test.notice.value).toContain('Postava uložena');
    expect(transport.mock.calls.filter(([, init]) => init.method === 'POST')).toHaveLength(2);
  });
  it('reconciles a persisted user turn after a lost provider response and never replays the POST', async () => {
    await render(); test.consent.value = true; test.text.value = 'Tell me about the wand.';
    const persisted = { ...detail(), conversation: { id: 'lab', turns: [
      { id: 'user-1', role: 'user', text: 'Tell me about the wand.' },
      { id: 'assistant-1', role: 'assistant', text: 'A real provider reply.' },
    ] } } as unknown as TestSessionDetail;
    transport.mockImplementation((path: string, init: RequestInit) => {
      if (path.includes('/persona-lab/sessions/lab/turns'))
        return Promise.resolve(Response.json({ code: 'gateway_timeout' }, { status: 504 }));
      if (path.endsWith('/testing-sessions/test')) return Promise.resolve(Response.json(persisted));
      return Promise.resolve(Response.json([]));
    });
    await test.send();
    expect(test.selected.value?.conversation.turns).toHaveLength(2);
    expect(test.text.value).toBe('');
    expect(test.busy.value).toBe(false);
    expect(test.turnPhase.value).toBe('idle');
    expect(transport.mock.calls.filter(call =>
      String(call[0]).includes('/persona-lab/sessions/lab/turns') && call[1]?.method === 'POST')).toHaveLength(1);
  });
  it('keeps an unpersisted draft after a failed turn for an explicit, user initiated retry', async () => {
    await render(); test.consent.value = true; test.text.value = 'This turn was not saved.';
    const clean = { ...detail(), conversation: { id: 'lab', turns: [] } } as unknown as TestSessionDetail;
    transport.mockImplementation((path: string, init: RequestInit) => {
      if (path.includes('/persona-lab/sessions/lab/turns'))
        return Promise.resolve(Response.json({ code: 'provider_unavailable' }, { status: 502 }));
      if (path.endsWith('/testing-sessions/test')) return Promise.resolve(Response.json(clean));
      return Promise.resolve(Response.json([]));
    });
    await test.send();
    expect(test.text.value).toBe('This turn was not saved.');
    expect(test.error.value).toBe('provider_unavailable');
    expect(test.busy.value).toBe(false);
    expect(test.turnPhase.value).toBe('idle');
  });
  it('allows the next turn when unrelated session and character collection reads fail', async () => {
    await render(); test.consent.value = true;
    let failCollections = false;
    const turns: { id: string; role: 'user' | 'assistant'; text: string }[] = [];
    const withTurns = () => ({ ...detail(), conversation: { id: 'lab', turns } }) as unknown as TestSessionDetail;
    transport.mockImplementation((path: string) => {
      if (path === '/app/v1/testing-sessions' || path === '/app/v1/testing-sessions/characters') {
        return failCollections
          ? Promise.resolve(Response.json({ code: 'rate_limited' }, { status: 429, headers: { 'Retry-After': '1' } }))
          : Promise.resolve(Response.json([]));
      }
      if (path === '/app/v1/testing-sessions/test') return Promise.resolve(Response.json(withTurns()));
      if (path.includes('/persona-lab/sessions/lab/turns')) {
        const ordinal = turns.length / 2 + 1;
        turns.push({ id: `user-${ordinal}`, role: 'user', text: `Turn ${ordinal}` },
          { id: `assistant-${ordinal}`, role: 'assistant', text: `Reply ${ordinal}` });
        return Promise.resolve(Response.json({ id: 'lab', turns }));
      }
      return Promise.resolve(Response.json([]));
    });
    test.text.value = 'Turn 1'; await test.send();
    failCollections = true; await test.refresh();
    expect(test.stale.value).toBe(false);
    expect(test.error.value).toBe('rate_limited');
    test.text.value = 'Turn 2'; await test.send();
    expect(transport.mock.calls.filter(call => String(call[0]).includes('/persona-lab/sessions/lab/turns')
      && call[1]?.method === 'POST')).toHaveLength(2);
    expect(test.selected.value?.conversation.turns.map(turn => turn.text)).toEqual(['Turn 1', 'Reply 1', 'Turn 2', 'Reply 2']);
  });
  it('does not carry an unresolved turn draft or send lock from session A into resumed session B', async () => {
    await render(); test.consent.value = true;
    const sessionB = { ...detail(), session: { ...detail().session, id: 'other' },
      conversation: { id: 'lab-other', turns: [] } } as unknown as TestSessionDetail;
    let failSessionARead = false;
    let turnsB: unknown[] = [];
    transport.mockImplementation((path: string, init: RequestInit = {}) => {
      if (path === '/app/v1/testing-sessions/test') {
        return failSessionARead
          ? Promise.resolve(Response.json({ code: 'testing_request_failed' }, { status: 503 }))
          : Promise.resolve(Response.json(detail()));
      }
      if (path === '/app/v1/testing-sessions/other') return Promise.resolve(Response.json(sessionB));
      if (path === '/app/v1/testing-sessions' || path === '/app/v1/testing-sessions/characters') return Promise.resolve(Response.json([]));
      if (path.includes('/persona-lab/sessions/lab/turns')) {
        failSessionARead = true;
        return Promise.resolve(Response.json({ code: 'gateway_timeout' }, { status: 504 }));
      }
      if (path.includes('/persona-lab/sessions/lab-other/turns')) {
        turnsB = [{ id: 'user-b', role: 'user', text: 'Turn B', createdAt: '2026-10-25T10:00:00Z' },
          { id: 'assistant-b', role: 'assistant', text: 'Reply B', createdAt: '2026-10-25T10:00:01Z' }];
        sessionB.conversation.turns = turnsB as TestSessionDetail['conversation']['turns'];
        return Promise.resolve(Response.json({ id: 'lab-other', turns: turnsB }));
      }
      return Promise.resolve(Response.json([]));
    });
    test.text.value = 'Uncertain turn A'; await test.send();
    expect(test.uncertainTurn.value).toBe(true);
    expect(test.text.value).toBe('');

    await test.resume('other');
    expect(test.selected.value?.session.id).toBe('other');
    expect(test.uncertainTurn.value).toBe(false);
    expect(test.text.value).toBe('');
    await test.refresh();
    expect(test.uncertainTurn.value).toBe(false);
    expect(test.text.value).toBe('');

    test.text.value = 'Turn B'; await test.send();
    expect(transport.mock.calls.filter(call => String(call[0]).includes('/persona-lab/sessions/lab-other/turns')
      && call[1]?.method === 'POST')).toHaveLength(1);
    expect(test.selected.value?.conversation.turns.map(turn => turn.text)).toEqual(['Turn B', 'Reply B']);
    expect(turnsB).toHaveLength(2);
  });
  it('shows server operation phases while the turn POST is held without replacing turns or allowing overlap', async () => {
    vi.useFakeTimers(); await render(); test.consent.value = true; test.text.value = 'Long provider turn';
    const userTurn = { id: 'user-final', role: 'user' as const, text: 'Long provider turn', createdAt: '2026-10-25T10:00:00Z' };
    const assistantTurn = { id: 'assistant-final', role: 'assistant' as const, text: 'Provider response', createdAt: '2026-10-25T10:00:01Z' };
    const finalDetail = { ...detail(), conversation: { ...detail().conversation, turns: [userTurn, assistantTurn] },
      conversationActive: false, conversationOperation: null } as unknown as TestSessionDetail;
    let releasePost!: (response: Response) => void;
    let postReturned = false; let progressReads = 0;
    transport.mockImplementation((path: string, init: RequestInit = {}) => {
      if (path.includes('/persona-lab/sessions/lab/turns')) {
        return new Promise<Response>(resolve => { releasePost = resolve; });
      }
      if (path === '/app/v1/testing-sessions/test') {
        if (postReturned) return Promise.resolve(Response.json(finalDetail));
        progressReads++;
        const phase = progressReads === 1 ? 'connecting_provider' : 'waiting_provider';
        return Promise.resolve(Response.json({ ...detail(), conversationActive: true,
          conversationOperation: { operationId: 'op-live', phase, elapsedMs: progressReads * 2000 } }));
      }
      return Promise.resolve(Response.json([]));
    });
    const sending = test.send(); await flushPromises();
    expect(test.busy.value).toBe(true);
    await vi.advanceTimersByTimeAsync(2000); await flushPromises();
    expect(test.selected.value?.conversationOperation?.phase).toBe('connecting_provider');
    expect(test.selected.value?.conversation.turns).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(2000); await flushPromises();
    expect(test.selected.value?.conversationOperation?.phase).toBe('waiting_provider');
    expect(test.selected.value?.conversation.turns).toHaveLength(0);
    test.text.value = 'A second turn'; await test.send();
    expect(transport.mock.calls.filter(call => String(call[0]).includes('/persona-lab/sessions/lab/turns')
      && call[1]?.method === 'POST')).toHaveLength(1);

    postReturned = true;
    releasePost(Response.json({ ...detail().conversation, turns: [userTurn, assistantTurn] }));
    await sending; await flushPromises();
    expect(test.selected.value?.conversation.turns.map(turn => turn.text)).toEqual(['Long provider turn', 'Provider response']);
    expect(test.selected.value?.conversationOperation).toBeNull();
    expect(test.busy.value).toBe(false);
  });
});
