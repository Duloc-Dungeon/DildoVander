import { ref } from 'vue';
import { createPresentationGate, type PresentationEvent } from './presentationEffects';
import selectCue from '../assets/wns-v2/audio/ui-select.wav';
import confirmCue from '../assets/wns-v2/audio/ui-confirm.wav';
import revealCue from '../assets/wns-v2/audio/impact.wav';

interface Player {
  play(): Promise<unknown>;
  pause(): void;
  volume: number;
  onended: ((...args: any[]) => any) | null;
}

export function createSoundBus(makePlayer: (url: string) => Player = url => new Audio(url)) {
  const enabled = ref(false);
  const gate = createPresentationGate();
  let foreground = true;
  let last = -Infinity;
  const active = new Set<Player>();
  const cooldown = new Map<string, number>();
  function stop() { for (const player of active) player.pause(); active.clear(); }
  function play(url: string, key: string, now: number, interval: number) {
    if (!enabled.value || !foreground || now - last < 45 || now - (cooldown.get(key) ?? -Infinity) < interval || active.size >= 2) return false;
    const player = makePlayer(url); player.volume = .65;
    player.onended = () => active.delete(player); active.add(player);
    last = now; cooldown.set(key, now);
    void player.play().catch(() => active.delete(player));
    return true;
  }
  return {
    enabled,
    enable(value: boolean) { enabled.value = value; gate.setEnabled(value); if (!value) stop(); },
    visible(value: boolean) { foreground = value; gate.setVisible(value); if (!value) stop(); },
    scope(value: string) { gate.setScope(value); stop(); },
    prime(sequence: number, revision: number) { gate.prime(sequence, revision); },
    local(cue: 'select' | 'confirm', now = Date.now()) {
      return play(cue === 'select' ? selectCue : confirmCue, cue, now, cue === 'select' ? 120 : 300);
    },
    // Only already-authorized server events may enter here. No private cues,
    // result inference from polling, global truth, speech or ambient playback.
    projected(event: PresentationEvent, now = Date.now()) {
      return gate.accept(event, now) && event.type === 'round-reveal' && play(revealCue, event.type, now, 300);
    },
  };
}
export const wnsSound = createSoundBus();
