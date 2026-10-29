import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AbortError, TimeoutError } from '@microsoft/signalr';

const api = vi.hoisted(() => ({ transport: vi.fn() }));
vi.mock('../identity/session', () => ({ accountTransport: api.transport }));
import { GameLiveHttpClient } from './gameLive';

beforeEach(() => { api.transport.mockReset(); });
afterEach(() => { vi.useRealTimers(); });

describe('authenticated SignalR transport', () => {
  it('routes negotiate and poll through the account bridge with supported headers', async () => {
    api.transport.mockResolvedValue(new Response('handshake'));
    const client = new GameLiveHttpClient();
    const response = await client.send({ method: 'POST', url: '/app/v1/game/live/negotiate?negotiateVersion=1', content: '{}', headers: { 'X-SignalR-User-Agent': 'diagnostic' } });
    expect(response.content).toBe('handshake');
    const [path, request] = api.transport.mock.calls[0];
    expect(path).toBe('/app/v1/game/live/negotiate?negotiateVersion=1');
    expect(request.headers.has('X-SignalR-User-Agent')).toBe(false);
    expect(request.headers.get('Content-Type')).toBe('text/plain;charset=UTF-8');
    expect(request.signal).toBeInstanceOf(AbortSignal);
  });

  it.each(['https://other.invalid/app/v1/game/live', '/app/v1/admin/users', '/app/v1/game/live/other'])('rejects a redirected or unrelated endpoint %s', async url => {
    await expect(new GameLiveHttpClient().send({ method: 'GET', url })).rejects.toThrow('Invalid game live URL');
    expect(api.transport).not.toHaveBeenCalled();
  });

  it('cancels an in-flight native poll on stop and releases its abort callback', async () => {
    const signal = { aborted: false, onabort: null as (() => void) | null };
    api.transport.mockImplementation((_path, request) => new Promise((_resolve, reject) => {
      request.signal.addEventListener('abort', () => reject(new Error('cancelled')));
    }));
    const pending = new GameLiveHttpClient().send({ method: 'GET', url: '/app/v1/game/live?id=one', abortSignal: signal });
    const assertion = expect(pending).rejects.toBeInstanceOf(AbortError);
    signal.onabort!();
    await assertion;
    expect(signal.onabort).toBeNull();
  });

  it('reports a poll timeout using the SignalR retryable timeout type', async () => {
    vi.useFakeTimers();
    api.transport.mockImplementation((_path, request) => new Promise((_resolve, reject) => {
      request.signal.addEventListener('abort', () => reject(new Error('cancelled')));
    }));
    const pending = new GameLiveHttpClient().send({ method: 'GET', url: '/app/v1/game/live?id=one', timeout: 50 });
    const assertion = expect(pending).rejects.toBeInstanceOf(TimeoutError);
    await vi.advanceTimersByTimeAsync(50);
    await assertion;
  });
});
