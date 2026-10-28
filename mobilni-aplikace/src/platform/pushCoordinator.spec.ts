import { beforeEach, describe, expect, it } from 'vitest';
import { pausePushReconnection, pushReconnectionCheck, resumePushReconnection, serializePushMutation } from './pushCoordinator';

beforeEach(() => resumePushReconnection());

describe('push reconnection ordering', () => {
  it('completes an admitted token write before revoking and rejects stale and intervening callbacks', async () => {
    let finish!: () => void;
    const writes: string[] = [];
    const oldCheck = pushReconnectionCheck();
    const tokenWrite = serializePushMutation(async () => {
      oldCheck();
      await new Promise<void>(resolve => { finish = resolve; });
      writes.push('token');
    });
    await Promise.resolve();
    pausePushReconnection();
    const revoke = serializePushMutation(async () => { writes.push('revoke'); });
    const duringRevokeCheck = pushReconnectionCheck();
    finish(); await tokenWrite; await revoke;
    expect(writes).toEqual(['token', 'revoke']);
    expect(oldCheck).toThrow();
    expect(duringRevokeCheck).toThrow();
    resumePushReconnection();
    expect(oldCheck).toThrow();
    expect(() => pushReconnectionCheck()()).not.toThrow();
  });

  it('does not poison later mutations after a failure', async () => {
    await expect(serializePushMutation(async () => { throw new Error('network'); })).rejects.toThrow('network');
    expect(await serializePushMutation(async () => 'revoked')).toBe('revoked');
  });
});
