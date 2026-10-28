export type WorkshopVoicePhase = 'off' | 'listening' | 'processing' | 'speaking';
interface VoiceObserver { phase(value: WorkshopVoicePhase): void; level(value: number): void }

/** Recorded answers are drafts. Only explicit submission can advance a server step. */
export class WorkshopVoice {
  private context?: AudioContext;
  private output?: AudioBufferSourceNode;
  private stream?: MediaStream;
  private recorder?: MediaRecorder;
  private frame?: number;
  private deadline?: ReturnType<typeof setTimeout>;
  private cancelRecording?: () => void;
  private epoch = 0;

  constructor(private observer: VoiceObserver) {}

  async unlock(): Promise<void> {
    this.context ??= new AudioContext();
    await this.context.resume();
  }

  async play(audio: Blob): Promise<void> {
    const current = ++this.epoch;
    this.stopOutput();
    const context = this.context;
    if (!context) throw new Error('Nejdřív zapni zvuk.');
    const buffer = await context.decodeAudioData(await audio.arrayBuffer());
    if (current !== this.epoch || context.state === 'closed') return;
    const source = context.createBufferSource();
    const analyser = context.createAnalyser();
    analyser.fftSize = 256;
    source.buffer = buffer;
    source.connect(analyser);
    analyser.connect(context.destination);
    const data = new Uint8Array(analyser.fftSize);
    this.output = source;
    source.onended = () => {
      if (this.output !== source) return;
      this.stopOutput();
      this.observer.phase('off');
    };
    source.start();
    this.observer.phase('speaking');
    const measure = () => {
      if (this.output !== source) return;
      analyser.getByteTimeDomainData(data);
      const energy = data.reduce((sum, sample) => sum + ((sample - 128) / 128) ** 2, 0) / data.length;
      this.observer.level(Math.min(1, Math.sqrt(energy) * 4));
      this.frame = requestAnimationFrame(measure);
    };
    measure();
  }

  async record(): Promise<Blob | null> {
    this.stop();
    if (typeof MediaRecorder === 'undefined' || !navigator.mediaDevices?.getUserMedia) {
      throw new Error('Tady mikrofon není dostupný. Můžeš odpovědět psaním.');
    }
    const current = this.epoch;
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    if (current !== this.epoch) { stream.getTracks().forEach(track => track.stop()); return null; }
    this.stream = stream;
    const mimeType = ['audio/webm;codecs=opus', 'audio/mp4', 'audio/ogg;codecs=opus'].find(type => MediaRecorder.isTypeSupported(type));
    let recorder: MediaRecorder;
    try { recorder = new MediaRecorder(stream, mimeType ? { mimeType } : undefined); }
    catch (cause) { this.releaseInput(); throw cause; }
    this.recorder = recorder;
    return new Promise((resolve, reject) => {
      const chunks: Blob[] = [];
      let size = 0;
      let cancelled = false;
      this.cancelRecording = () => { cancelled = true; };
      recorder.ondataavailable = event => {
        chunks.push(event.data); size += event.data.size;
        if (size > 8 * 1024 * 1024) { cancelled = true; this.finishRecording(); }
      };
      recorder.onerror = () => {
        if (this.recorder === recorder) this.releaseInput();
        stream.getTracks().forEach(track => track.stop());
        reject(new Error('Nahrávání selhalo. Odpověz psaním.'));
      };
      recorder.onstop = () => {
        if (this.recorder === recorder) this.releaseInput();
        stream.getTracks().forEach(track => track.stop());
        const result = new Blob(chunks, { type: recorder.mimeType });
        if (current === this.epoch) this.observer.phase('off');
        resolve(cancelled || !result.size ? null : result);
      };
      try { recorder.start(1000); }
      catch (cause) { this.releaseInput(); reject(cause); return; }
      this.observer.phase('listening');
      // A bounded recording is still only a draft, never an accepted answer.
      this.deadline = setTimeout(() => this.finishRecording(), 90000);
    });
  }

  finishRecording(): void {
    if (this.recorder?.state === 'recording') this.recorder.stop();
  }

  stop(): void {
    ++this.epoch;
    this.cancelRecording?.();
    this.finishRecording();
    this.releaseInput();
    this.stopOutput();
    this.observer.phase('off');
  }

  dispose(): void {
    this.stop();
    void this.context?.close();
    this.context = undefined;
  }

  private releaseInput(): void {
    clearTimeout(this.deadline);
    this.stream?.getTracks().forEach(track => track.stop());
    this.stream = undefined;
    this.recorder = undefined;
    this.cancelRecording = undefined;
  }

  private stopOutput(): void {
    if (this.frame !== undefined) cancelAnimationFrame(this.frame);
    this.frame = undefined;
    const output = this.output;
    this.output = undefined;
    if (output) { output.onended = null; output.stop(); output.disconnect(); }
    this.observer.level(0);
  }
}
