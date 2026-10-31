import { defineComponent } from 'vue';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useSparkCompetition } from './useSparkCompetition';
import type { SparkReceipt, SparkStandings, SparkTicket } from './sparkApi';
const api = vi.hoisted(() => ({ read: vi.fn(), issue: vi.fn(), finish: vi.fn() }));
vi.mock('./sparkApi', () => ({ readSparkStandings: api.read, issueSparkTicket: api.issue, finishSparkRun: api.finish }));
vi.mock('../identity/session', () => ({ account: { value: { id: 'one' } } }));
vi.mock('../game/game', () => ({ operation: () => crypto.randomUUID(), GameError: class GameError extends Error { constructor(public status: number, public code: string) { super(code); } } }));
let wrapper: VueWrapper;
let competition: ReturnType<typeof useSparkCompetition>;
const standings = (best = 0): SparkStandings => ({ rulesVersion: 'spark-v3', playerId: 'one', groupId: 'group', best, ranking: [] });
const ticket = (runId: string): SparkTicket => ({ runId, seed: 1, expiresAt: Date.now() + 7200000, rulesVersion: 'spark-v3' });
const replay = { endTick: 79, taps: [] };
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
function start() { wrapper = mount(defineComponent({ setup() { competition = useSparkCompetition(); return () => null; } })); }
beforeEach(() => {
  localStorage.clear(); vi.resetAllMocks();
  api.read.mockResolvedValue(standings()); api.issue.mockResolvedValue(ticket('next'));
  api.finish.mockResolvedValue({ score: 1, best: 1, newBest: true });
});
afterEach(() => wrapper?.unmount());

describe('ranked spark delivery', () => {
  it.each(['spark-v1', 'spark-v2'])('submits restored %s runs without inflating the current v3 record', async (rulesVersion) => {
    const legacyTicket = { ...ticket('legacy'), rulesVersion };
    localStorage.setItem('wns.spark.pending.v1.one', JSON.stringify([{ ticket: legacyTicket, replay }]));
    const nextStandings = deferred<SparkStandings>();
    api.read.mockResolvedValueOnce(standings(2)).mockReturnValueOnce(nextStandings.promise);
    api.finish.mockResolvedValue({ score: 100, best: 100, newBest: true });
    start(); await competition.refresh(); await competition.retry();
    expect(api.finish.mock.calls[0]!.slice(0, 2)).toEqual(['legacy', replay]);
    expect(competition.best.value).toBe(2);
    expect(competition.pending.value).toHaveLength(0);
    nextStandings.resolve(standings(2)); await flushPromises();
  });
  it('drains a run that finishes while the previous result is still saving', async () => {
    const first = deferred<SparkReceipt>(); api.finish.mockReturnValueOnce(first.promise);
    start(); competition.submit(ticket('a'), replay); competition.submit(ticket('b'), replay);
    expect(api.finish).toHaveBeenCalledTimes(1);
    first.resolve({ score: 1, best: 1, newBest: true }); await flushPromises();
    expect(api.finish.mock.calls.map(call => call[0])).toEqual(['a', 'b']);
    expect(competition.pending.value).toHaveLength(0);
    expect(JSON.parse(localStorage.getItem('wns.spark.pending.v1.one')!)).toEqual([]);
  });
  it('preserves unsent runs after failure and retries the exact ticket and replay', async () => {
    api.finish.mockRejectedValueOnce(new Error('offline')); start();
    competition.submit(ticket('a'), replay); await flushPromises();
    expect(competition.pending.value).toHaveLength(1);
    await competition.retry();
    expect(api.finish.mock.calls[1]!.slice(0, 2)).toEqual(['a', replay]);
    expect(competition.pending.value).toHaveLength(0);
  });
  it('does not let a prefetched ticket overflow a full durable queue', async () => {
    start(); await competition.prepare();
    competition.pending.value = Array.from({ length: 5 }, (_, i) => ({ ticket: ticket(String(i)), replay }));
    expect(competition.ticket.value).toBeUndefined();
    expect(competition.takeTicket()).toBeUndefined();
  });
  it('restores all valid old entries instead of silently truncating a legacy queue', () => {
    const stored = Array.from({ length: 6 }, (_, i) => ({ ticket: ticket(String(i)), replay }));
    localStorage.setItem('wns.spark.pending.v1.one', JSON.stringify(stored));
    start(); expect(competition.pending.value).toHaveLength(6);
  });
  it('ignores an older standings response that arrives after a newer snapshot', async () => {
    const old = deferred<SparkStandings>(); api.read.mockReturnValueOnce(old.promise).mockResolvedValueOnce(standings(9));
    start(); const first = competition.refresh(); await competition.refresh(); old.resolve(standings(2)); await first;
    expect(competition.best.value).toBe(9);
  });
  it('preserves the issuance operation id when the response is lost', async () => {
    api.issue.mockRejectedValueOnce(new Error('response lost')); start();
    await competition.prepare(); await competition.prepare();
    expect(api.issue.mock.calls[0]![1]).toBe(api.issue.mock.calls[1]![1]);
  });
  it('unmount aborts delivery without deleting the persisted replay', async () => {
    const finish = deferred<SparkReceipt>(); api.finish.mockReturnValue(finish.promise);
    start(); competition.submit(ticket('a'), replay); wrapper.unmount();
    expect(api.finish.mock.calls[0]![2].aborted).toBe(true);
    finish.resolve({ score: 1, best: 1, newBest: true }); await flushPromises();
    expect(JSON.parse(localStorage.getItem('wns.spark.pending.v1.one')!)).toHaveLength(1);
  });
});
