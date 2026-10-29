<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import { wnsSound } from '../../application/wnsSound';
import { accountTransport } from '../identity/session';
import { GameError, gameErrorText, gameRequest, type Match } from './game';

const props = defineProps<{ attempt: Pick<Match, 'id' | 'revision' | 'status'> }>();
interface Narration { revision: number; text: string; voiceAvailable: boolean }

const narration = ref<Narration>();
const loading = ref(false);
const textError = ref('');
const voiceError = ref('');
const voiceEnabled = ref(false);
const voiceUnlocking = ref(false);
const audioContext = shallowRef<AudioContext>();
const currentKey = computed(() => `${props.attempt.id}:${props.attempt.revision}`);
let narrationController: AbortController | undefined;
let audioController: AbortController | undefined;
let source: AudioBufferSourceNode | undefined;
let narrationGeneration = 0;
let audioGeneration = 0;
let voiceToggleGeneration = 0;
let pendingAudioKey = '';
let playedKey = '';
let disposed = false;

function stopAudioRequest(): void {
  audioController?.abort();
  audioController = undefined;
}

function stopPlayback(): void {
  audioGeneration++;
  pendingAudioKey = '';
  stopAudioRequest();
  if (source) {
    source.onended = null;
    try { source.stop(); } catch { /* It may already have ended. */ }
    try { source.disconnect(); } catch { /* The context may already be closed. */ }
    source = undefined;
  }
}

function isCurrent(key: string, token: number): boolean {
  return !disposed && token === audioGeneration && key === currentKey.value
    && document.visibilityState !== 'hidden' && wnsSound.enabled.value;
}

async function speak(item: Narration, replay = false): Promise<void> {
  const key = `${props.attempt.id}:${item.revision}`;
  if (!voiceEnabled.value || !wnsSound.enabled.value || document.visibilityState === 'hidden'
    || !item.voiceAvailable || !item.text || pendingAudioKey === key || (!replay && playedKey === key)) return;
  const context = audioContext.value;
  if (!context) return;
  stopPlayback();
  const token = audioGeneration;
  const controller = new AbortController();
  audioController = controller;
  pendingAudioKey = key;
  voiceError.value = '';
  try {
    const response = await accountTransport(`/app/v1/game/boss/attempts/${encodeURIComponent(props.attempt.id)}/audio`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ expectedRevision: item.revision }), signal: controller.signal,
    });
    if (!response.ok) throw new Error('Baronův hlas se nepodařilo načíst. Zkus to znovu.');
    const contentType = response.headers.get('content-type') ?? '';
    if (!/^audio\/mpeg(?:\s*;|$)/i.test(contentType)) throw new Error('Baronův hlas má neznámý formát.');
    const blob = await response.blob();
    if (blob.size === 0 || blob.size > 512 * 1024) throw new Error('Baronův hlas má neplatnou velikost.');
    if (!isCurrent(key, token)) return;
    const buffer = await context.decodeAudioData(await blob.arrayBuffer());
    if (!isCurrent(key, token) || context.state === 'closed') return;
    const output = context.createBufferSource();
    output.buffer = buffer;
    output.connect(context.destination);
    output.onended = () => {
      try { output.disconnect(); } catch { /* The context may already be closed. */ }
      if (source === output) source = undefined;
    };
    source = output;
    playedKey = key;
    output.start();
  } catch (cause) {
    if (isCurrent(key, token)) voiceError.value = cause instanceof Error ? cause.message : 'Baronův hlas se nepodařilo přehrát.';
  } finally {
    if (audioController === controller) audioController = undefined;
    if (pendingAudioKey === key) pendingAudioKey = '';
  }
}

async function loadNarration(): Promise<void> {
  const token = ++narrationGeneration;
  narrationController?.abort();
  narrationController = undefined;
  stopPlayback();
  narration.value = undefined;
  textError.value = '';
  voiceError.value = '';
  const { id, revision, status } = props.attempt;
  if (!['active', 'complete'].includes(status) || document.visibilityState === 'hidden') { loading.value = false; return; }
  const controller = new AbortController();
  narrationController = controller;
  loading.value = true;
  try {
    const result = await gameRequest<Narration>(`/boss/attempts/${encodeURIComponent(id)}/narration`, { expectedRevision: revision }, controller.signal);
    if (disposed || controller.signal.aborted || token !== narrationGeneration || currentKey.value !== `${id}:${revision}`) return;
    if (result.revision !== revision) throw new Error('Baronova odpověď patří k jiné verzi zápasu. Obnov ji znovu.');
    narration.value = result;
    if (voiceEnabled.value) void speak(result);
  } catch (cause) {
    if (!disposed && !controller.signal.aborted && token === narrationGeneration && currentKey.value === `${id}:${revision}`) {
      textError.value = narrationErrorText(cause);
    }
  } finally {
    if (narrationController === controller) {
      narrationController = undefined;
      loading.value = false;
    }
  }
}

function narrationErrorText(cause: unknown): string {
  if (cause instanceof GameError && cause.code.startsWith('boss_narration_')) {
    if (cause.code === 'boss_narration_busy') return 'Baron právě skládá odpověď. Zkus ji načíst za chvíli.';
    if (cause.code === 'boss_narration_not_ready') return 'Baronova odpověď se ještě připravuje. Zkus to za chvíli.';
    if (cause.code === 'boss_narration_revision_conflict') return 'Zápas se mezitím změnil. Obnov Baronovu odpověď.';
    return 'Baronovu odpověď se nepodařilo načíst. Zkus to znovu.';
  }
  return gameErrorText(cause);
}

async function toggleVoice(): Promise<void> {
  if (voiceUnlocking.value) {
    voiceToggleGeneration++;
    voiceUnlocking.value = false;
    return;
  }
  if (voiceEnabled.value) {
    voiceToggleGeneration++;
    voiceEnabled.value = false;
    stopPlayback();
    voiceError.value = '';
    return;
  }
  const token = ++voiceToggleGeneration;
  voiceUnlocking.value = true;
  if (!wnsSound.enabled.value) wnsSound.enable(true);
  try {
    const context = audioContext.value ?? new AudioContext();
    audioContext.value = context;
    await context.resume();
    if (disposed || token !== voiceToggleGeneration) return;
    voiceEnabled.value = true;
    if (narration.value) void speak(narration.value);
  } catch {
    if (!disposed && token === voiceToggleGeneration) voiceError.value = 'Zvuk se nepodařilo zapnout. Můžeš dál číst Baronovu odpověď.';
  } finally {
    if (token === voiceToggleGeneration) voiceUnlocking.value = false;
  }
}

function onVisibilityChange(): void {
  if (document.visibilityState === 'hidden') {
    stopPlayback();
    if (narrationController) {
      narrationGeneration++;
      narrationController.abort();
      narrationController = undefined;
      loading.value = false;
    }
    return;
  }
  if (!narration.value && ['active', 'complete'].includes(props.attempt.status)) void loadNarration();
  else if (voiceEnabled.value && wnsSound.enabled.value && narration.value) void speak(narration.value);
}

watch([
  () => props.attempt.id,
  () => props.attempt.revision,
  () => props.attempt.status,
], () => { void loadNarration(); }, { immediate: true });
watch(wnsSound.enabled, enabled => {
  if (!enabled) stopPlayback();
  else if (voiceEnabled.value && narration.value && document.visibilityState !== 'hidden') void speak(narration.value);
});
onMounted(() => document.addEventListener('visibilitychange', onVisibilityChange));
onBeforeUnmount(() => {
  disposed = true;
  voiceToggleGeneration++;
  narrationController?.abort();
  stopPlayback();
  document.removeEventListener('visibilitychange', onVisibilityChange);
  void audioContext.value?.close();
});
</script>

<template>
  <section class="boss-narration" aria-labelledby="boss-narration-title">
    <h2 id="boss-narration-title">Baron má slovo</h2>
    <p v-if="narration?.text" class="boss-narration__text" aria-live="polite">{{ narration.text }}</p>
    <p v-else-if="loading" role="status">Baron si rovná myšlenky.</p>
    <p v-if="textError" class="boss-narration__error" role="alert">{{ textError }} <button type="button" @click="loadNarration">Zkusit znovu</button></p>
    <p v-if="narration && !narration.voiceAvailable" class="boss-narration__hint">Baronův hlas dnes nedorazil. Odpověď zůstává k přečtení.</p>
    <div class="boss-narration__controls">
      <button type="button" :aria-pressed="voiceEnabled" @click="toggleVoice">{{ voiceEnabled ? 'Vypnout Baronův hlas' : voiceUnlocking ? 'Zrušit zapnutí hlasu' : 'Zapnout Baronův hlas' }}</button>
      <button v-if="voiceEnabled && narration?.voiceAvailable && voiceError" type="button" @click="speak(narration, true)">Zkusit hlas znovu</button>
    </div>
    <p v-if="voiceError" class="boss-narration__error" role="status">{{ voiceError }}</p>
    <p v-if="voiceEnabled && !wnsSound.enabled.value" class="boss-narration__hint">Zvuk hry je vypnutý.</p>
  </section>
</template>

<style scoped>
.boss-narration { margin: 1rem 0; padding: 1rem; border: 1px solid var(--aw-border, currentColor); border-radius: var(--aw-radius-md, .75rem); }
.boss-narration h2 { margin: 0 0 .5rem; font-size: 1.05rem; }
.boss-narration__text { margin: 0 0 .75rem; line-height: 1.55; }
.boss-narration__controls { display: flex; flex-wrap: wrap; gap: .5rem; }
.boss-narration button { min-height: 2.75rem; padding: .5rem .8rem; color: inherit; background: transparent; border: 1px solid currentColor; border-radius: .4rem; font: inherit; cursor: pointer; }
.boss-narration__error { color: var(--aw-danger, #a52b36); }
.boss-narration__hint { opacity: .75; font-size: .9rem; }
</style>
