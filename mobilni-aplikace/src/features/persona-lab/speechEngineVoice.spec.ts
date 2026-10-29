import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Callbacks } from '@elevenlabs/client';
import { SpeechEngineVoice } from './speechEngineVoice';
import { LabVoice, type VoiceObserver } from './voice';
import type { SocketTicket } from './contracts';

const sdk = vi.hoisted(() => ({ start: vi.fn() }));
vi.mock('@elevenlabs/client', () => ({ Conversation: { startSession: sdk.start } }));

describe('Speech Engine control and WebRTC ownership', () => {
  let callbacks: Callbacks;
  let observer: VoiceObserver;
  let voice: SpeechEngineVoice;
  let socket: FakeSocket;
  let rtc: ReturnType<typeof fakeRtc>;
  const ticket = {
    url: 'wss://lab.example/app/v1/customer/wand/workshop/sessions/test/live?ticket=once',
    path: '', expiresAt: '', speechEngine: { conversationToken: 'ephemeral-token', firstMessage: 'Co má tvoje hůlka chránit?' },
  };
  function fakeRtc() {
    return { endSession: vi.fn().mockResolvedValue(undefined), setVolume: vi.fn(), setMicMuted: vi.fn(), getOutputVolume: vi.fn().mockReturnValue(.4) };
  }
  class FakeSocket {
    static OPEN = 1;
    readyState = 1;
    onmessage: ((event: { data: unknown }) => void) | null = null;
    onopen: (() => void) | null = null;
    onclose: ((event: { code: number }) => void) | null = null;
    onerror: (() => void) | null = null;
    send = vi.fn();
    close = vi.fn();
    constructor(public address: string) { socket = this; }
  }
  function event(value: object) { socket.onmessage?.({ data: JSON.stringify(value) }); }
  async function connect() { await voice.connect(ticket, 'test', 'https://lab.example'); }

  beforeEach(() => {
    vi.useFakeTimers();
    rtc = fakeRtc();
    sdk.start.mockReset().mockImplementation(async options => { callbacks = options; return rtc; });
    observer = {
      phase: vi.fn(), transcript: vi.fn(), turn: vi.fn(), session: vi.fn(), error: vi.fn(), closed: vi.fn(),
      workshopSession: vi.fn(), workshopTranscript: vi.fn(), userPartial: vi.fn(), interrupted: vi.fn(), outputLevel: vi.fn(),
    };
    voice = new SpeechEngineVoice(observer);
    vi.stubGlobal('WebSocket', FakeSocket);
  });
  afterEach(() => { voice.dispose(); vi.unstubAllGlobals(); vi.useRealTimers(); });

  it('starts RTC before control readiness and waits for both transports', async () => {
    await connect();
    expect(sdk.start).toHaveBeenCalledWith(expect.objectContaining({
      conversationToken: 'ephemeral-token', connectionType: 'webrtc', textOnly: false,
      overrides: { agent: { firstMessage: ticket.speechEngine.firstMessage } },
    }));
    expect(socket.address).toBe(ticket.url);
    expect(observer.phase).toHaveBeenLastCalledWith('connecting');
    event({ type: 'ready', playbackMode: 'stream' });
    expect(observer.phase).toHaveBeenLastCalledWith('listening');
    callbacks.onModeChange?.({ mode: 'speaking' });
    expect(observer.phase).toHaveBeenLastCalledWith('speaking');
    event({ type: 'state', session: { id: 'state' } });
    expect(observer.workshopSession).toHaveBeenCalledWith({ id: 'state' });
    vi.advanceTimersByTime(100);
    expect(observer.outputLevel).toHaveBeenLastCalledWith(.4);
  });

  it('routes a workshop ticket to the SDK without acquiring the PCM microphone pipeline', async () => {
    const microphone = vi.fn(); const close = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { mediaDevices: { getUserMedia: microphone } });
    vi.stubGlobal('AudioContext', class {
      resume = vi.fn().mockResolvedValue(undefined); close = close; onstatechange = null;
    });
    const lab = new LabVoice(observer);
    await lab.connect(async () => ticket, 'test', 'https://lab.example', { purpose: 'workshop' });
    expect(close).toHaveBeenCalledOnce(); expect(microphone).not.toHaveBeenCalled();
    expect(sdk.start).toHaveBeenCalledOnce();
    event({ type: 'ready', playbackMode: 'stream' });
    lab.setOutputVolume(.2); lab.interrupt();
    expect(rtc.setVolume).toHaveBeenLastCalledWith({ volume: 0 });
    lab.dispose(); expect(rtc.endSession).toHaveBeenCalledOnce();
  });

  it('keeps speculative user revisions display-only and confirmed snapshots authoritative', async () => {
    await connect(); event({ type: 'ready', playbackMode: 'stream' });
    callbacks.onIncomingEvent?.({ type: 'tentative_user_transcript', tentative_user_transcription_event: { user_transcript: 'Moje hůlka', event_id: 3 } });
    expect(observer.userPartial).toHaveBeenLastCalledWith('Moje hůlka');
    expect(observer.workshopTranscript).not.toHaveBeenCalled();
    expect(socket.send.mock.calls).toEqual([[JSON.stringify({ type: 'speech_engine_activity', eventId: 3 })]]);
    const snapshot = { role: 'user', sequence: 1, text: 'Moje hůlka.', final: true };
    event({ type: 'speech_engine_user_committed', eventId: 3 });
    event({ type: 'workshop_transcript', ...snapshot });
    expect(observer.userPartial).toHaveBeenLastCalledWith('');
    expect(observer.workshopTranscript).toHaveBeenLastCalledWith(snapshot);
  });

  it('mutes cancelled speech until a new server reply and preserves chosen volume and microphone', async () => {
    await connect(); event({ type: 'ready', playbackMode: 'stream' });
    voice.setOutputVolume(.35); voice.setMuted(true);
    expect(rtc.setMicMuted).toHaveBeenLastCalledWith(true);
    event({ type: 'speech_engine_speaking', eventId: 1, sequence: 1 });
    voice.interrupt();
    expect(socket.send).toHaveBeenLastCalledWith(JSON.stringify({ type: 'cancel' }));
    expect(rtc.setVolume).toHaveBeenLastCalledWith({ volume: 0 });
    callbacks.onModeChange?.({ mode: 'speaking' });
    event({ type: 'speech_engine_speaking', eventId: 1, sequence: 1 });
    expect(rtc.setVolume).toHaveBeenLastCalledWith({ volume: 0 });
    event({ type: 'interrupted' });
    expect(observer.interrupted).toHaveBeenCalledOnce();
    // The final verdict legitimately reuses the closing reply's upstream ID.
    event({ type: 'speech_engine_speaking', eventId: 1, sequence: 2 });
    expect(rtc.setVolume).toHaveBeenLastCalledWith({ volume: .35 });
    voice.setOutputEnabled(false);
    event({ type: 'speech_engine_speaking', eventId: 2, sequence: 3 });
    expect(rtc.setVolume).toHaveBeenLastCalledWith({ volume: 0 });
  });

  it('mutes SDK interruption without sending an unversioned activity or fake transcript', async () => {
    await connect(); event({ type: 'ready', playbackMode: 'stream' });
    callbacks.onInterruption?.({ event_id: 1 });
    expect(socket.send).not.toHaveBeenCalled();
    expect(rtc.setVolume).toHaveBeenLastCalledWith({ volume: 0 });
    expect(observer.turn).not.toHaveBeenCalled();
    expect(observer.transcript).not.toHaveBeenCalled();
  });

  it('does not queue unversioned SDK interruptions before the control socket is ready', async () => {
    await connect(); socket.readyState = 0;
    callbacks.onInterruption?.({ event_id: 1 });
    callbacks.onInterruption?.({ event_id: 2 });
    expect(socket.send).not.toHaveBeenCalled();
    socket.readyState = 1; socket.onopen?.();
    expect(socket.send).not.toHaveBeenCalled();
    event({ type: 'ready', playbackMode: 'stream' });
    expect(socket.send).not.toHaveBeenCalled();
    socket.onopen?.();
    expect(socket.send).not.toHaveBeenCalled();
  });

  it('deduplicates replies by sequence rather than the reusable upstream event ID', async () => {
    await connect(); event({ type: 'ready', playbackMode: 'stream' });
    event({ type: 'speech_engine_speaking', eventId: 8, sequence: 2 });
    voice.interrupt();
    event({ type: 'speech_engine_speaking', eventId: 9, sequence: 1 });
    expect(rtc.setVolume).toHaveBeenLastCalledWith({ volume: 0 });
    event({ type: 'speech_engine_speaking', eventId: 8, sequence: 3 });
    expect(rtc.setVolume).toHaveBeenLastCalledWith({ volume: 1 });
  });

  it('ignores older tentative IDs and preserves retirement after confirmed app snapshots', async () => {
    await connect(); event({ type: 'ready', playbackMode: 'stream' });
    const partial = (eventId: number, text: string) => callbacks.onIncomingEvent?.({
      type: 'tentative_user_transcript', tentative_user_transcription_event: { user_transcript: text, event_id: eventId },
    });
    partial(3, 'Starší věta'); partial(4, 'Novější věta'); partial(3, 'Opožděná stará věta');
    expect(observer.userPartial).toHaveBeenLastCalledWith('Novější věta');
    partial(4, 'Novější opravená věta');
    expect(observer.userPartial).toHaveBeenLastCalledWith('Novější opravená věta');
    event({ type: 'speech_engine_user_committed', eventId: 4 });
    event({ type: 'workshop_transcript', role: 'user', sequence: 1, text: 'Potvrzená věta.', final: true });
    partial(4, 'Nesmí se vrátit'); partial(3, 'Ani předchozí');
    expect(observer.userPartial).toHaveBeenLastCalledWith('');
    partial(5, 'Příští věta');
    callbacks.onIncomingEvent?.({ type: 'user_transcript', user_transcription_event: { user_transcript: 'Předchozí potvrzení.', event_id: 4 } });
    expect(observer.userPartial).toHaveBeenLastCalledWith('Příští věta');
    callbacks.onIncomingEvent?.({ type: 'user_transcript', user_transcription_event: { user_transcript: 'Příští věta.', event_id: 5 } });
    partial(5, 'Opožděný náhled potvrzené věty');
    expect(observer.userPartial).toHaveBeenLastCalledWith('');
    expect(observer.error).not.toHaveBeenCalled();
  });

  it('retires a committed SDK ID even when no tentative preview preceded it', async () => {
    await connect();
    callbacks.onIncomingEvent?.({ type: 'user_transcript', user_transcription_event: { user_transcript: 'Potvrzená věta.', event_id: 8 } });
    callbacks.onIncomingEvent?.({ type: 'tentative_user_transcript', tentative_user_transcription_event: { user_transcript: 'Opožděný náhled', event_id: 8 } });
    expect(observer.userPartial).toHaveBeenLastCalledWith('');
    callbacks.onIncomingEvent?.({ type: 'tentative_user_transcript', tentative_user_transcription_event: { user_transcript: 'Nová věta', event_id: 9 } });
    expect(observer.userPartial).toHaveBeenLastCalledWith('Nová věta');
  });

  it('retires an unseen committed control ID before a delayed SDK preview can revive it', async () => {
    await connect(); event({ type: 'ready', playbackMode: 'stream' });
    event({ type: 'speech_engine_user_committed', eventId: 8 });
    event({ type: 'workshop_transcript', role: 'user', sequence: 1, text: 'Potvrzená věta.', final: true });
    callbacks.onIncomingEvent?.({ type: 'tentative_user_transcript', tentative_user_transcription_event: { user_transcript: 'Opožděný náhled', event_id: 8 } });
    expect(observer.userPartial).toHaveBeenLastCalledWith('');
    expect(socket.send).not.toHaveBeenCalled();
    callbacks.onIncomingEvent?.({ type: 'tentative_user_transcript', tentative_user_transcription_event: { user_transcript: 'Nová věta', event_id: 9 } });
    event({ type: 'speech_engine_user_committed', eventId: 8 });
    event({ type: 'workshop_transcript', role: 'user', sequence: 1, text: 'Potvrzená věta.', final: true });
    expect(observer.userPartial).toHaveBeenLastCalledWith('Nová věta');
  });

  it('queues one activity per new tentative turn without committing its text', async () => {
    await connect(); socket.readyState = 0;
    const partial = (eventId: number, text: string) => callbacks.onIncomingEvent?.({
      type: 'tentative_user_transcript', tentative_user_transcription_event: { user_transcript: text, event_id: eventId },
    });
    partial(3, 'Náhled'); partial(3, 'Opravený náhled');
    expect(socket.send).not.toHaveBeenCalled();
    socket.readyState = 1; socket.onopen?.(); event({ type: 'ready', playbackMode: 'stream' });
    expect(socket.send.mock.calls).toEqual([[JSON.stringify({ type: 'speech_engine_activity', eventId: 3 })]]);
    partial(3, 'Další oprava'); partial(4, 'Nová věta'); partial(4, 'Nová věta upravená'); partial(3, 'Opožděná stará');
    expect(socket.send.mock.calls).toEqual([3, 4].map(eventId => [JSON.stringify({ type: 'speech_engine_activity', eventId })]));
    expect(observer.turn).not.toHaveBeenCalled(); expect(observer.workshopTranscript).not.toHaveBeenCalled();
  });

  it.each(['sdk', 'control'])('drops queued activity retired by a %s confirmation before readiness', async source => {
    await connect(); socket.readyState = 0;
    callbacks.onIncomingEvent?.({ type: 'tentative_user_transcript', tentative_user_transcription_event: { user_transcript: 'Náhled', event_id: 3 } });
    if (source === 'sdk') {
      callbacks.onIncomingEvent?.({ type: 'user_transcript', user_transcription_event: { user_transcript: 'Potvrzená věta.', event_id: 3 } });
    } else event({ type: 'speech_engine_user_committed', eventId: 3 });
    socket.readyState = 1; socket.onopen?.(); event({ type: 'ready', playbackMode: 'stream' });
    callbacks.onIncomingEvent?.({ type: 'tentative_user_transcript', tentative_user_transcription_event: { user_transcript: 'Opožděný náhled', event_id: 3 } });
    expect(socket.send).not.toHaveBeenCalled();
    callbacks.onIncomingEvent?.({ type: 'tentative_user_transcript', tentative_user_transcription_event: { user_transcript: 'Nový náhled', event_id: 4 } });
    expect(socket.send.mock.calls).toEqual([[JSON.stringify({ type: 'speech_engine_activity', eventId: 4 })]]);
  });

  it('retains the newest queued activity when an earlier SDK turn is committed', async () => {
    await connect(); socket.readyState = 0;
    for (const eventId of [3, 4]) callbacks.onIncomingEvent?.({ type: 'tentative_user_transcript', tentative_user_transcription_event: { user_transcript: 'Náhled', event_id: eventId } });
    callbacks.onIncomingEvent?.({ type: 'user_transcript', user_transcription_event: { user_transcript: 'Předchozí věta.', event_id: 3 } });
    socket.readyState = 1; socket.onopen?.(); event({ type: 'ready', playbackMode: 'stream' });
    expect(socket.send.mock.calls).toEqual([[JSON.stringify({ type: 'speech_engine_activity', eventId: 4 })]]);
  });

  it.each([0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1])('rejects invalid user preview ID %s', async eventId => {
    await connect();
    callbacks.onIncomingEvent?.({ type: 'tentative_user_transcript', tentative_user_transcription_event: { user_transcript: 'Náhled', event_id: eventId } });
    expect(observer.error).toHaveBeenCalledWith('invalid_server_event', false);
  });

  it('closes both connections and ignores callbacks after dispose', async () => {
    await connect();
    voice.dispose();
    expect(socket.close).toHaveBeenCalledOnce(); expect(rtc.endSession).toHaveBeenCalledOnce();
    callbacks.onError?.('secret provider diagnostic'); callbacks.onDisconnect?.({ reason: 'user' });
    expect(observer.error).not.toHaveBeenCalled();
    expect(socket.onmessage).toBeNull();
  });

  it('ends an RTC connection that resolves after disposal', async () => {
    let complete!: (value: typeof rtc) => void;
    sdk.start.mockImplementation(options => { callbacks = options; return new Promise(resolve => { complete = resolve; }); });
    const pending = connect();
    await vi.waitFor(() => expect(sdk.start).toHaveBeenCalledOnce());
    voice.dispose(); complete(rtc); await pending;
    expect(rtc.endSession).toHaveBeenCalledOnce();
    expect(observer.phase).not.toHaveBeenCalledWith('listening');
  });

  it.each([
    { type: 'ready', playbackMode: 'acknowledged' },
    { type: 'workshop_transcript', role: 'user', text: 'bad', sequence: 0, final: true },
    { type: 'speech_engine_speaking', eventId: '1' },
    { type: 'speech_engine_speaking', eventId: 1, sequence: 0 },
    { type: 'speech_engine_speaking', eventId: 1, sequence: '1' },
    { type: 'speech_engine_user_committed', eventId: 0 },
  ])('rejects malformed control protocol: %j', async value => {
    await connect(); event(value);
    expect(observer.error).toHaveBeenCalledWith('invalid_server_event', false);
    expect(rtc.endSession).toHaveBeenCalledOnce();
  });

  it('rejects a different control origin before giving its token to the SDK', async () => {
    await voice.connect({ ...ticket, url: ticket.url.replace('lab.example', 'other.example') }, 'test', 'https://lab.example');
    expect(sdk.start).not.toHaveBeenCalled();
    expect(observer.error).toHaveBeenCalledWith('invalid_socket_address', false);
  });

  it.each([undefined, null, 123, '', '   ', 'a'.repeat(1801)])('rejects malformed server first message %j before starting the SDK', async firstMessage => {
    await voice.connect({ ...ticket, speechEngine: { conversationToken: 'ephemeral-token', firstMessage } } as unknown as SocketTicket, 'test', 'https://lab.example');
    expect(sdk.start).not.toHaveBeenCalled();
    expect(observer.error).toHaveBeenCalledWith('invalid_server_event', false);
  });

  it('passes the server greeting unchanged at the maximum supported length', async () => {
    const firstMessage = 'Ř'.repeat(1800);
    await voice.connect({ ...ticket, speechEngine: { ...ticket.speechEngine, firstMessage } }, 'test', 'https://lab.example');
    expect(sdk.start).toHaveBeenCalledWith(expect.objectContaining({ overrides: { agent: { firstMessage } } }));
    expect(observer.error).not.toHaveBeenCalled();
  });

  it('fails and cleans up both transports on a readiness timeout', async () => {
    await connect(); vi.advanceTimersByTime(30000);
    expect(observer.error).toHaveBeenCalledWith('connection_timeout', expect.any(Boolean));
    expect(socket.close).toHaveBeenCalledOnce(); expect(rtc.endSession).toHaveBeenCalledOnce();
  });
});
