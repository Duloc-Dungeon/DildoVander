import { afterEach, describe, expect, it, vi } from 'vitest';
const transport=vi.hoisted(()=>vi.fn());
vi.mock('../identity/session',()=>({accountTransport:transport}));
import { TestingSessionsClient } from './client';
afterEach(()=>vi.clearAllMocks());
describe('testing account transport boundaries',()=>{
  it('retains the server trace for JSON and non-JSON failures without exposing the response body', async () => {
    const requestId = '01234567-89ab-4cde-8f01-23456789abcd';
    const client = new TestingSessionsClient(() => 'owner');
    transport.mockResolvedValueOnce(new Response('<html>gateway detail</html>', { status: 502, headers: { 'X-Request-Id': requestId } }));
    await expect(client.characters()).rejects.toMatchObject({ code: 'testing_request_failed', status: 502, requestId });
    transport.mockResolvedValueOnce(Response.json({ code: 'account_required', traceId: requestId }, { status: 401 }));
    await expect(client.characters()).rejects.toMatchObject({ code: 'account_required', status: 401, requestId });
  });
  it('honors an empty 429 response and suppresses requests until Retry-After elapses', async () => {
    const requestId = '01234567-89ab-4cde-8f01-23456789abcd';
    const client = new TestingSessionsClient(() => 'owner');
    transport.mockResolvedValueOnce(new Response(null, { status: 429, headers: { 'Retry-After': '30', 'X-Request-Id': requestId } }));
    await expect(client.characters()).rejects.toMatchObject({ code: 'rate_limited', status: 429, requestId, retryAfterMs: 30000 });
    await expect(client.sessions()).rejects.toMatchObject({ code: 'rate_limited', status: 429, requestId });
    expect(client.cooldownRemainingMs()).toBeGreaterThan(0);
    expect(transport).toHaveBeenCalledTimes(1);
  });
  it('preserves Retry-After and its request ID across same-account invalidation', async () => {
    const requestId = '01234567-89ab-4cde-8f01-23456789abcd';
    const client = new TestingSessionsClient(() => 'owner');
    transport.mockResolvedValueOnce(new Response(null, { status: 429, headers: { 'Retry-After': '30', 'X-Request-Id': requestId } }));
    await expect(client.characters()).rejects.toMatchObject({ code: 'rate_limited', requestId });
    client.invalidate();
    await expect(client.sessions()).rejects.toMatchObject({ code: 'rate_limited', status: 429, requestId });
    expect(client.cooldownRemainingMs()).toBeGreaterThan(0);
    expect(transport).toHaveBeenCalledTimes(1);
  });
  it('fences a response that arrives after account replacement even when transport ignores cancellation',async()=>{
    let owner='first';let complete!:(response:Response)=>void;
    transport.mockImplementation(()=>new Promise<Response>(resolve=>{complete=resolve;}));
    const client=new TestingSessionsClient(()=>owner);const response=client.session('owned-session');
    owner='second';client.invalidate(true);complete(Response.json({private:'first-account'}));
    await expect(response).rejects.toMatchObject({name:'AbortError'});
    expect(transport.mock.calls[0][1].signal.aborted).toBe(true);
  });
  it('fences an asset body that finishes after the account changes', async () => {
    let owner = 'first';
    let releaseBlob!: (blob: Blob) => void;
    let started!: () => void;
    const blobStarted = new Promise<void>(resolve => { started = resolve; });
    const response = new Response(new Blob(['image'], { type: 'image/png' }));
    response.blob = () => {
      started();
      return new Promise<Blob>(resolve => { releaseBlob = resolve; });
    };
    transport.mockResolvedValueOnce(response);
    const client = new TestingSessionsClient(() => owner);
    const asset = client.asset('session', 'keepsake', 'asset');
    await blobStarted;
    owner = 'second';
    client.invalidate();
    releaseBlob(new Blob(['private first-account image'], { type: 'image/png' }));
    await expect(asset).rejects.toMatchObject({ name: 'AbortError' });
    expect(transport.mock.calls[0][1].signal.aborted).toBe(true);
  });
  it('keeps virtual scope identifiers on explicit account routes and retains backend failure codes',async()=>{
    transport.mockResolvedValue(Response.json({code:'test_resource_unavailable'},{status:404}));
    const client=new TestingSessionsClient(()=>'owner');
    await expect(client.selectWand('unowned')).rejects.toMatchObject({code:'test_resource_unavailable',status:404});
    expect(transport).toHaveBeenCalledWith('/app/v1/testing-sessions/unowned/select',expect.objectContaining({method:'POST',body:'{}',redirect:'error'}));
    expect(transport.mock.calls[0][1].headers).not.toHaveProperty('Authorization');
  });
  it('refuses to render a successful non-image response as generated artwork',async()=>{
    transport.mockResolvedValue(new Response('fake-success',{headers:{'Content-Type':'text/plain'}}));
    await expect(new TestingSessionsClient(()=>'owner').asset('test','keepsake','asset')).rejects.toMatchObject({code:'testing_asset_unavailable'});
  });
});
