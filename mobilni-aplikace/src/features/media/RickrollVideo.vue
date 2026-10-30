<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';

const props = defineProps<{ src: string; active: boolean }>();
const emit = defineEmits<{ dismiss: [] }>();
const video = ref<HTMLVideoElement>();
const failed = ref(false);
const autoplayBlocked = ref(false);
const playedSeconds = ref(0);
const remaining = computed(() => Math.max(0, Math.ceil(10 - playedSeconds.value)));
const canDismiss = computed(() => failed.value || remaining.value === 0);
let lastMediaTime = 0;
let generation = 0;

function trackPlayback(): void {
  const element = video.value;
  if (!element || !props.active) return;
  const elapsed = element.currentTime - lastMediaTime;
  // Count continuous playback, never a seek to the end of the video.
  if (!element.seeking && elapsed > 0 && elapsed <= 1.5) playedSeconds.value += elapsed;
  lastMediaTime = element.currentTime;
}
function dismiss(): void { if (canDismiss.value) emit('dismiss'); }
async function startPlayback(): Promise<void> {
  try { await video.value?.play(); autoplayBlocked.value = false; }
  catch { failed.value = true; }
}

watch(() => props.active, async active => {
  const token = ++generation;
  failed.value = false;
  autoplayBlocked.value = false;
  playedSeconds.value = 0;
  lastMediaTime = 0;
  await nextTick();
  const element = video.value;
  if (!element || token !== generation) return;
  if (!active) { element.pause(); return; }
  element.currentTime = 0;
  element.volume = 0.8;
  element.muted = false;
  try { await element.play(); }
  catch (error) {
    if (token !== generation) return;
    if (error instanceof DOMException && error.name === 'NotAllowedError') autoplayBlocked.value = true;
    else failed.value = true;
  }
}, { immediate: true, flush: 'post' });

onBeforeUnmount(() => {
  generation++;
  video.value?.pause();
  video.value?.removeAttribute('src');
  video.value?.load();
});
</script>

<template>
  <section v-show="active" class="rickroll-video" aria-label="Rick Astley - Never Gonna Give You Up">
    <h1 id="rickroll-title">Rick Astley - Never Gonna Give You Up</h1>
    <video ref="video" :src="src" :controls="canDismiss" playsinline preload="auto" @timeupdate="trackPlayback" @error="failed = true" @ended="dismiss" />
    <p v-if="failed" role="status">Video se nepodařilo načíst.</p>
    <template v-else-if="autoplayBlocked">
      <p role="status">Prohlížeč zablokoval automatické přehrávání. Spusť video tlačítkem.</p>
      <button type="button" @click="startPlayback">Přehrát video</button>
    </template>
    <p v-if="!canDismiss" class="rickroll-countdown" role="timer" aria-label="Odpočet do přeskočení">Přeskočit za {{ remaining }} s</p>
    <button type="button" :disabled="!canDismiss" @click="dismiss">{{ canDismiss ? 'Pokračovat' : 'Ještě chvilku. Rick se loučí nerad.' }}</button>
  </section>
</template>

<style scoped>
.rickroll-video { display: grid; align-content: center; gap: 1.5rem; min-height: 100dvh; padding: max(1rem, env(safe-area-inset-top)) 1rem max(1rem, env(safe-area-inset-bottom)); background: #000; color: #fff; text-align: center; }
.rickroll-video h1 { margin: 0; font-size: clamp(1.25rem, 4vw, 2rem); }
.rickroll-video video { width: min(100%, 70rem); max-height: 75dvh; aspect-ratio: 16 / 9; justify-self: center; }
.rickroll-video button { justify-self: center; min-height: 44px; padding: .5rem 1rem; color: inherit; background: transparent; border: 1px solid currentColor; border-radius: .5rem; cursor: pointer; }
.rickroll-video button:focus-visible { outline: 2px solid currentColor; outline-offset: 4px; }
.rickroll-video button:disabled { opacity: .65; cursor: default; }
.rickroll-countdown { margin: 0; font-size: 1.25rem; font-variant-numeric: tabular-nums; }
</style>
