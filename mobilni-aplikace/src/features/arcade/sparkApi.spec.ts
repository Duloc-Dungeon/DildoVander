import { beforeEach, describe, expect, it, vi } from 'vitest';
import { issueSparkTicket, readSparkStandings } from './sparkApi';

const request = vi.hoisted(() => vi.fn());
vi.mock('../game/game', () => ({ gameRequest: request, operation: () => 'test-operation' }));
beforeEach(() => request.mockReset());

describe('spark rules negotiation', () => {
  it('explicitly requests v3 tickets and standings', async () => {
    const ticket = { runId: 'run', seed: 1, rulesVersion: 'spark-v3', expiresAt: Date.now() + 60000 };
    request.mockResolvedValueOnce(ticket);
    expect(await issueSparkTicket()).toEqual(ticket);
    expect(request).toHaveBeenLastCalledWith('/arcade/runs', { operationId: 'test-operation', rulesVersion: 'spark-v3' }, undefined);
    const standings = { rulesVersion: 'spark-v3', best: 1, ranking: [] };
    request.mockResolvedValueOnce(standings);
    expect(await readSparkStandings()).toEqual(standings);
    expect(request).toHaveBeenLastCalledWith('/arcade?rulesVersion=spark-v3', undefined, undefined);
  });

  it.each(['spark-v1', 'spark-v2'])('rejects older %s responses', async (rulesVersion) => {
    request.mockResolvedValueOnce({ runId: 'run', seed: 1, rulesVersion, expiresAt: Date.now() + 60000 });
    await expect(issueSparkTicket()).rejects.toThrow('Unsupported spark rules');
    request.mockResolvedValueOnce({ rulesVersion, best: 100, ranking: [] });
    await expect(readSparkStandings()).rejects.toThrow('Unsupported spark rules');
  });
});
