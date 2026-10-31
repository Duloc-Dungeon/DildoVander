import { beforeEach, describe, expect, it, vi } from 'vitest';
import { issueSparkTicket, readSparkStandings } from './sparkApi';

const request = vi.hoisted(() => vi.fn());
vi.mock('../game/game', () => ({ gameRequest: request, operation: () => 'test-operation' }));
beforeEach(() => request.mockReset());

describe('spark rules negotiation', () => {
  it('explicitly requests v2 tickets and standings', async () => {
    const ticket = { runId: 'run', seed: 1, rulesVersion: 'spark-v2', expiresAt: Date.now() + 60000 };
    request.mockResolvedValueOnce(ticket);
    expect(await issueSparkTicket()).toEqual(ticket);
    expect(request).toHaveBeenLastCalledWith('/arcade/runs', { operationId: 'test-operation', rulesVersion: 'spark-v2' }, undefined);
    const standings = { rulesVersion: 'spark-v2', best: 1, ranking: [] };
    request.mockResolvedValueOnce(standings);
    expect(await readSparkStandings()).toEqual(standings);
    expect(request).toHaveBeenLastCalledWith('/arcade?rulesVersion=spark-v2', undefined, undefined);
  });

  it('rejects an old server response instead of playing or ranking v2 as v1', async () => {
    request.mockResolvedValueOnce({ runId: 'run', seed: 1, rulesVersion: 'spark-v1', expiresAt: Date.now() + 60000 });
    await expect(issueSparkTicket()).rejects.toThrow('Unsupported spark rules');
    request.mockResolvedValueOnce({ rulesVersion: 'spark-v1', best: 100, ranking: [] });
    await expect(readSparkStandings()).rejects.toThrow('Unsupported spark rules');
  });
});
