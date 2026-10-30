import { wnsSound } from '../../application/wnsSound';

/** Short synthesized cues avoid network/audio decoding on the input path. */
export function createSparkAudio() {
  let context: AudioContext | undefined;
  return {
    unlock() {
      if (!wnsSound.enabled.value) return;
      try { context ??= new AudioContext(); void context.resume().catch(() => {}); } catch { /* Sound is optional. */ }
    },
    play(cue: 'turn' | 'gate' | 'record' | 'crash') {
      if (!wnsSound.enabled.value || context?.state !== 'running') return;
      const now = context.currentTime;
      const duration = cue === 'crash' ? .16 : cue === 'turn' ? .035 : .12;
      const oscillator = context.createOscillator();
      const gain = context.createGain();
      oscillator.type = cue === 'crash' ? 'triangle' : 'sine';
      oscillator.frequency.setValueAtTime({ turn: 420, gate: 880, record: 1320, crash: 160 }[cue], now);
      oscillator.frequency.exponentialRampToValueAtTime(cue === 'crash' ? 50 : cue === 'record' ? 1760 : 660, now + duration);
      gain.gain.setValueAtTime(.0001, now); gain.gain.exponentialRampToValueAtTime(.06, now + .008);
      gain.gain.exponentialRampToValueAtTime(.0001, now + duration);
      oscillator.connect(gain).connect(context.destination);
      oscillator.start(now); oscillator.stop(now + duration);
      oscillator.onended = () => { oscillator.disconnect(); gain.disconnect(); };
    },
    destroy() { if (context) void context.close().catch(() => {}); context = undefined; },
  };
}
