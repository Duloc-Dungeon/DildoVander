import type { VoiceConversation } from '@elevenlabs/client';
import rawAudioProcessorUrl from '@elevenlabs/client/worklets/rawAudioProcessor.js?url&no-inline';
import type { SocketTicket } from './contracts';
import { classifyLabFailure } from './errors';
import { socketUrl, type VoiceObserver } from './voice';

/** The app socket owns workshop state; the SDK alone owns microphone and audio. */
export class SpeechEngineVoice {
  private socket: WebSocket | null = null;
  private conversation: VoiceConversation | null = null;
  private generation = 0;
  private ready = false;
  private connected = false;
  private timeout: ReturnType<typeof setTimeout> | null = null;
  private meter: ReturnType<typeof setInterval> | null = null;
  private volume = 1;
  private outputEnabled = true;
  private cancelled = false;
  private muted = false;
  private mode: 'speaking' | 'listening' = 'listening';
  private lastSpeakingSequence = 0;
  private pendingActivityId: number | null = null;
  private latestUserEvent = 0;
  private retiredUserEvent = 0;
  private finishing = false;
  private finishTimeout: ReturnType<typeof setTimeout> | null = null;

  constructor(private observer: VoiceObserver) {}

  async connect(ticket: SocketTicket, sessionId: string, origin: string): Promise<void> {
    this.dispose();
    const generation = this.generation;
    const current = () => generation === this.generation;
    this.observer.phase('connecting');
    try {
      const token = ticket.speechEngine?.conversationToken;
      const firstMessage = ticket.speechEngine?.firstMessage;
      if (typeof token !== 'string' || !token.trim() || token.length > 32768)
        throw Object.assign(new Error('Invalid Speech Engine ticket.'), { code: 'invalid_server_event' });
      if (typeof firstMessage !== 'string' || !firstMessage.trim() || firstMessage.length > 1800)
        throw Object.assign(new Error('Invalid Speech Engine first message.'), { code: 'invalid_server_event' });
      const address = socketUrl(ticket, sessionId, origin, 'workshop');
      const socket = new WebSocket(address);
      this.socket = socket;
      socket.onopen = () => { if (current()) this.flushActivity(); };
      this.timeout = setTimeout(() => { if (current()) this.fail('connection_timeout'); }, 30000);
      socket.onmessage = event => {
        if (!current()) return;
        try {
          if (typeof event.data !== 'string') throw new Error('Unexpected audio on control socket.');
          this.receive(JSON.parse(event.data));
        } catch { if (current()) this.fail('invalid_server_event'); }
      };
      socket.onerror = () => { if (current()) this.fail('connection_failed'); };
      socket.onclose = event => {
        if (current() && !this.finishing) this.fail(event.code === 1008 ? 'connection_policy_rejected' : 'connection_closed');
      };
      // Both transports must start together: server readiness waits for the RTC client.
      const { Conversation } = await import('@elevenlabs/client');
      if (!current()) return;
      const conversation = await Conversation.startSession({
        conversationToken: token, connectionType: 'webrtc', textOnly: false,
        workletPaths: { rawAudioProcessor: rawAudioProcessorUrl },
        overrides: { agent: { firstMessage } },
        onError: () => { if (current()) this.fail('elevenlabs_connection_failed'); },
        onDisconnect: () => { if (current()) { if (this.finishing) this.finish(); else this.fail('connection_closed'); } },
        onModeChange: ({ mode }) => {
          if (!current()) return;
          this.mode = mode;
          if (this.finishing && mode === 'listening') { this.finish(); return; }
          this.publishPhase();
        },
        onInterruption: () => {
          if (!current()) return;
          this.cancelled = true; this.applyVolume();
        },
        onIncomingEvent: (event: unknown) => {
          if (!current() || !event || typeof event !== 'object' || !('type' in event)) return;
          if (!['user_transcript', 'tentative_user_transcript'].includes(String(event.type))) return;
          const partial = (event as Record<string, { user_transcript?: unknown; event_id?: unknown }>)[
            event.type === 'user_transcript' ? 'user_transcription_event' : 'tentative_user_transcription_event'
          ];
          if (!partial || typeof partial.user_transcript !== 'string' || partial.user_transcript.length > 1800
            || !Number.isSafeInteger(partial.event_id) || Number(partial.event_id) < 1) {
            this.fail('invalid_server_event'); return;
          }
          const eventId = Number(partial.event_id);
          if (event.type === 'user_transcript') {
            this.retireUserEvent(eventId);
            return;
          }
          if (eventId <= this.retiredUserEvent || eventId < this.latestUserEvent) return;
          if (eventId > this.latestUserEvent) {
            this.pendingActivityId = eventId; this.flushActivity();
          }
          this.latestUserEvent = eventId;
          // Display-only revision. Never submit tentative recognition as a workshop turn.
          this.observer.userPartial?.(partial.user_transcript);
        },
      });
      if (!current()) { await conversation.endSession(); return; }
      this.conversation = conversation;
      conversation.setMicMuted(this.muted);
      this.applyVolume(); this.connected = true; this.publishPhase();
      if (this.observer.outputLevel) this.meter = setInterval(() => {
        if (current()) this.observer.outputLevel?.(this.cancelled || !this.outputEnabled ? 0 : conversation.getOutputVolume());
      }, 100);
    } catch (error) {
      if (!current()) return;
      const code = error instanceof DOMException && ['NotAllowedError', 'PermissionDeniedError'].includes(error.name)
        ? 'microphone_permission' : error && typeof error === 'object' && 'code' in error && typeof error.code === 'string'
          ? error.code : 'elevenlabs_connection_failed';
      this.fail(code);
    }
  }

  private publishPhase(): void {
    if (!this.ready || !this.connected) return;
    if (this.timeout) clearTimeout(this.timeout);
    this.timeout = null;
    this.observer.phase(this.cancelled ? 'listening' : this.mode);
  }

  private receive(value: Record<string, unknown>): void {
    if (!value || typeof value !== 'object' || typeof value.type !== 'string') throw new Error('Invalid event.');
    switch (value.type) {
      case 'ready':
        if (this.ready || value.playbackMode !== 'stream') throw new Error('Invalid ready.');
        this.ready = true; this.flushActivity(); this.publishPhase(); break;
      case 'state':
        if (!value.session || typeof value.session !== 'object') throw new Error('Invalid state.');
        this.observer.workshopSession?.(value.session); break;
      case 'workshop_transcript':
        if (!this.ready || !['user', 'assistant'].includes(String(value.role)) || !Number.isSafeInteger(value.sequence)
          || Number(value.sequence) < 1 || typeof value.text !== 'string' || value.text.length > 1800 || typeof value.final !== 'boolean')
          throw new Error('Invalid transcript.');
        this.observer.workshopTranscript?.({ role: value.role as 'user' | 'assistant', sequence: Number(value.sequence), text: value.text, final: value.final });
        break;
      case 'speech_engine_user_committed':
        if (!Number.isSafeInteger(value.eventId) || Number(value.eventId) < 1) throw new Error('Invalid committed user event.');
        this.retireUserEvent(Number(value.eventId)); break;
      case 'speech_engine_speaking':
        if (!this.ready || !Number.isSafeInteger(value.eventId) || Number(value.eventId) < 0
          || !Number.isSafeInteger(value.sequence) || Number(value.sequence) < 1) throw new Error('Invalid speaking.');
        if (Number(value.sequence) <= this.lastSpeakingSequence) break;
        this.lastSpeakingSequence = Number(value.sequence);
        this.cancelled = false; this.applyVolume(); break;
      case 'interrupted':
        this.cancelled = true; this.applyVolume(); this.observer.phase('listening'); this.observer.interrupted?.(); break;
      case 'provider_ready':
        this.observer.providerReady?.(typeof value.providerSessionId === 'string' ? value.providerSessionId : null); break;
      case 'error': this.fail(typeof value.code === 'string' ? value.code : 'lab_provider_error'); break;
      case 'busy': this.observer.error('lab_session_busy', false); break;
      case 'ended':
        if (this.mode === 'speaking' && this.conversation) {
          // The control socket can finish before WebRTC has played its tail.
          // Keep output alive until the SDK reports actual playback completion.
          this.finishing = true;
          this.conversation.setMicMuted(true);
          if (!this.finishTimeout) this.finishTimeout = setTimeout(() => this.finish(), 120000);
        } else this.finish();
        break;
    }
  }

  private fail(code: string): void {
    this.dispose(); this.observer.phase('error');
    this.observer.error(code, classifyLabFailure(code).recoverable); this.observer.closed();
  }
  private finish(): void {
    this.dispose(); this.observer.phase('ended'); this.observer.closed();
  }
  private send(value: object): boolean {
    if (this.socket?.readyState !== WebSocket.OPEN) return false;
    this.socket.send(JSON.stringify(value)); return true;
  }
  private flushActivity(): void {
    // Readiness means the server installed the control handler, not just TCP open.
    if (this.pendingActivityId !== null && this.ready
      && this.send({ type: 'speech_engine_activity', eventId: this.pendingActivityId })) this.pendingActivityId = null;
  }
  private retireUserEvent(eventId: number): void {
    this.retiredUserEvent = Math.max(this.retiredUserEvent, eventId);
    if (this.pendingActivityId !== null && this.pendingActivityId <= this.retiredUserEvent) this.pendingActivityId = null;
    if (eventId >= this.latestUserEvent) this.observer.userPartial?.('');
    this.latestUserEvent = Math.max(this.latestUserEvent, eventId);
  }
  private applyVolume(): void {
    this.conversation?.setVolume({ volume: this.outputEnabled && !this.cancelled ? this.volume : 0 });
  }
  setOutputEnabled(value: boolean): void { this.outputEnabled = value; this.applyVolume(); }
  setOutputVolume(value: number): void {
    if (!Number.isFinite(value) || value < 0 || value > 1) throw new RangeError('Invalid speech volume.');
    this.volume = value; this.applyVolume();
  }
  setMuted(value: boolean): void { this.muted = value; this.conversation?.setMicMuted(value); }
  interrupt(): void {
    if (!this.ready) return;
    this.cancelled = true; this.applyVolume(); this.send({ type: 'cancel' }); this.observer.phase('waiting');
  }
  sendText(text: string): boolean { return this.ready && this.send({ type: 'text', text }); }
  end(): void { this.send({ type: 'end' }); this.dispose(); this.observer.phase('ended'); }
  dispose(): void {
    ++this.generation;
    if (this.finishTimeout) clearTimeout(this.finishTimeout);
    this.finishTimeout = null; this.finishing = false;
    this.mode = 'listening';
    if (this.timeout) clearTimeout(this.timeout);
    if (this.meter) clearInterval(this.meter);
    this.timeout = null; this.meter = null;
    const socket = this.socket; this.socket = null;
    if (socket) { socket.onopen = socket.onmessage = socket.onclose = socket.onerror = null; socket.close(); }
    const conversation = this.conversation; this.conversation = null;
    if (conversation) void conversation.endSession().catch(() => {});
    this.ready = this.connected = this.cancelled = false; this.pendingActivityId = null;
    this.lastSpeakingSequence = this.latestUserEvent = this.retiredUserEvent = 0;
    this.observer.userPartial?.(''); this.observer.outputLevel?.(0);
  }
}
