import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import GameBossNarration from './GameBossNarration.vue';
import { wnsSound } from '../../application/wnsSound';
import { GameError } from './game';

const api = vi.hoisted(() => ({ gameRequest: vi.fn(), transport: vi.fn() }));
vi.mock('./game', () => ({
  gameRequest: api.gameRequest,
  GameError: class GameError extends Error { constructor(public status: number, public code: string) { super(code); } },
  gameErrorText: (error: unknown) => error instanceof Error ? error.message : 'Chyba spojení.',
}));
vi.mock('../identity/session', () => ({ accountTransport: api.transport }));

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (cause: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

const sources: Array<{ start: ReturnType<typeof vi.fn>; stop: ReturnType<typeof vi.fn>; disconnect: ReturnType<typeof vi.fn>; onended: (() => void) | null }> = [];
let decode = vi.fn(async () => ({} as AudioBuffer));
let resumeGate: Promise<void> | undefined;
class AudioContextMock {
  state = 'running';
  destination = {} as AudioDestinationNode;
  resume = vi.fn(async () => { await resumeGate; });
  close = vi.fn(async () => undefined);
  decodeAudioData = (...args: Parameters<typeof decode>) => decode(...args);
  createBufferSource() {
    const source = { buffer: null, connect: vi.fn(), start: vi.fn(), stop: vi.fn(), disconnect: vi.fn(), onended: null as (() => void) | null };
    sources.push(source);
    return source as unknown as AudioBufferSourceNode;
  }
}

const attempt = (revision = 1) => ({ id: 'boss-1', revision, status: 'active' as const });
const reply = (revision: number, text = `Odpověď ${revision}.`) => ({ revision, text, voiceAvailable: true });
const mountView = (revision = 1) => mount(GameBossNarration, { props: { attempt: attempt(revision) } });

beforeEach(() => {
  api.gameRequest.mockReset();
  api.transport.mockReset();
  api.gameRequest.mockImplementation(async (_path: string, body: { expectedRevision: number }) => reply(body.expectedRevision));
  api.transport.mockResolvedValue({ ok: true, headers: new Headers({ 'content-type': 'audio/mpeg' }), blob: async () => new Blob(['mp3'], { type: 'audio/mpeg' }) });
  resumeGate = undefined;
  decode = vi.fn(async () => ({} as AudioBuffer));
  sources.length = 0;
  vi.stubGlobal('AudioContext', AudioContextMock);
  Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
  wnsSound.enable(true);
});

afterEach(() => {
  wnsSound.enable(false);
  vi.unstubAllGlobals();
});

describe('Baron narration', () => {
  it('discards an older narration response when the attempt revision changes', async () => {
    const first = deferred<ReturnType<typeof reply>>();
    const second = deferred<ReturnType<typeof reply>>();
    api.gameRequest.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const view = mountView(1);
    await view.setProps({ attempt: attempt(2) });
    first.resolve(reply(1, 'Zastaralá replika.'));
    await flushPromises();
    expect(view.text()).not.toContain('Zastaralá replika.');
    second.resolve(reply(2, 'Aktuální replika.'));
    await flushPromises();
    expect(view.text()).toContain('Aktuální replika.');
    view.unmount();
  });

  it('does not refetch or abort narration when polling replaces an unchanged attempt object', async () => {
    const pending = deferred<ReturnType<typeof reply>>();
    api.gameRequest.mockReturnValueOnce(pending.promise);
    const view = mountView();
    const signal = api.gameRequest.mock.calls[0]![2] as AbortSignal;
    await view.setProps({ attempt: { ...attempt(1) } });
    expect(api.gameRequest).toHaveBeenCalledOnce();
    expect(signal.aborted).toBe(false);
    pending.resolve(reply(1, 'Odpověď z původního požadavku.'));
    await flushPromises();
    expect(view.text()).toContain('Odpověď z původního požadavku.');
    view.unmount();
  });

  it('does not request or play audio until the guest explicitly enables the voice', async () => {
    const view = mountView();
    await flushPromises();
    expect(api.transport).not.toHaveBeenCalled();
    await view.get('button[aria-pressed="false"]').trigger('click');
    await flushPromises();
    expect(api.transport).toHaveBeenCalledOnce();
    expect(api.transport.mock.calls[0]![0]).toBe('/app/v1/game/boss/attempts/boss-1/audio');
    expect(JSON.parse(api.transport.mock.calls[0]![1].body)).toEqual({ expectedRevision: 1 });
    expect(sources).toHaveLength(1);
    expect(sources[0]!.start).toHaveBeenCalledOnce();
    view.unmount();
  });

  it('enables the global sound gate from the explicit Baron voice gesture', async () => {
    const view = mountView();
    await flushPromises();
    wnsSound.enable(false);
    await view.get('button[aria-pressed="false"]').trigger('click');
    await flushPromises();
    expect(wnsSound.enabled.value).toBe(true);
    expect(sources).toHaveLength(1);
    view.unmount();
  });

  it('keeps valid narration when global sound is muted during its request', async () => {
    const pending = deferred<ReturnType<typeof reply>>();
    api.gameRequest.mockReturnValueOnce(pending.promise);
    const view = mountView();
    wnsSound.enable(false);
    pending.resolve(reply(1, 'Odpověď dorazila potichu.'));
    await flushPromises();
    expect(view.text()).toContain('Odpověď dorazila potichu.');
    view.unmount();
  });

  it('does not enable voice if a second click cancels while audio unlock is pending', async () => {
    const gate = deferred<void>();
    resumeGate = gate.promise;
    const view = mountView();
    await flushPromises();
    const button = view.get('button[aria-pressed="false"]');
    await button.trigger('click');
    await button.trigger('click');
    gate.resolve();
    await flushPromises();
    expect(view.find('button[aria-pressed="false"]').exists()).toBe(true);
    expect(api.transport).not.toHaveBeenCalled();
    view.unmount();
  });

  it('stops current speech when global sound is muted and when the component unmounts', async () => {
    const view = mountView();
    await flushPromises();
    await view.get('button[aria-pressed="false"]').trigger('click');
    await flushPromises();
    const active = sources[0]!;
    wnsSound.enable(false);
    await flushPromises();
    expect(active.stop).toHaveBeenCalledOnce();
    await view.setProps({ attempt: attempt(2) });
    await flushPromises();
    wnsSound.enable(true);
    await flushPromises();
    expect(sources).toHaveLength(2);
    view.unmount();
    expect(sources[1]!.stop).toHaveBeenCalledOnce();
  });

  it('does not start audio whose decoding finishes after a newer revision arrives', async () => {
    const pendingDecode = deferred<AudioBuffer>();
    decode = vi.fn(() => pendingDecode.promise);
    api.gameRequest.mockImplementation(async (_path: string, body: { expectedRevision: number }) => ({
      ...reply(body.expectedRevision), voiceAvailable: body.expectedRevision !== 2,
    }));
    const view = mountView(1);
    await flushPromises();
    await view.get('button[aria-pressed="false"]').trigger('click');
    await flushPromises();
    expect(decode).toHaveBeenCalledOnce();
    await view.setProps({ attempt: attempt(2) });
    await flushPromises();
    pendingDecode.resolve({} as AudioBuffer);
    await flushPromises();
    expect(sources).toHaveLength(0);
    view.unmount();
  });

  it('keeps readable narration when voice playback fails', async () => {
    const view = mountView();
    await flushPromises();
    api.transport.mockRejectedValueOnce(new Error('Hlas se nepodařilo načíst.'));
    await view.get('button[aria-pressed="false"]').trigger('click');
    await flushPromises();
    expect(view.text()).toContain('Odpověď 1.');
    expect(view.text()).toContain('Hlas se nepodařilo načíst.');
    view.unmount();
  });

  it('shows narration-specific recovery text for narration failures', async () => {
    api.gameRequest.mockRejectedValueOnce(new GameError(503, 'boss_narration_unavailable'));
    const view = mountView();
    await flushPromises();
    expect(view.text()).toContain('Baronovu odpověď se nepodařilo načíst.');
    expect(view.text()).not.toContain('Hru se nepodařilo načíst');
    view.unmount();
  });

  it('rejects an unexpected audio media type before decoding', async () => {
    const view = mountView();
    await flushPromises();
    api.transport.mockResolvedValueOnce({ ok: true, headers: new Headers({ 'content-type': 'text/plain' }), blob: async () => new Blob(['not audio']) });
    await view.get('button[aria-pressed="false"]').trigger('click');
    await flushPromises();
    expect(decode).not.toHaveBeenCalled();
    expect(view.text()).toContain('Odpověď 1.');
    expect(view.text()).toContain('neznámý formát');
    view.unmount();
  });

  it('rejects audio larger than the endpoint limit before decoding', async () => {
    const view = mountView();
    await flushPromises();
    api.transport.mockResolvedValueOnce({ ok: true, headers: new Headers({ 'content-type': 'audio/mpeg' }), blob: async () => new Blob([new Uint8Array(512 * 1024 + 1)]) });
    await view.get('button[aria-pressed="false"]').trigger('click');
    await flushPromises();
    expect(decode).not.toHaveBeenCalled();
    expect(view.text()).toContain('Odpověď 1.');
    expect(view.text()).toContain('neplatnou velikost');
    view.unmount();
  });
});
