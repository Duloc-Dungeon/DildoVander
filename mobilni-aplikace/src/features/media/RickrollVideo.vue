<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue';

const props = defineProps<{ src: string; active: boolean }>();
const emit = defineEmits<{ dismiss: [] }>();
const video = ref<HTMLVideoElement>();
const failed = ref(false);
const autoplayBlocked = ref(false);
let generation = 0;

watch(() => props.active, async active => {
  const token = ++generation;
  failed.value = false;
  autoplayBlocked.value = false;
  await nextTick();
  const element = video.value;
  if (!element || token !== generation) return;
  if (!active) { element.pause(); return; }
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
    <video ref="video" :src="src" controls playsinline preload="auto" @error="failed = true" @ended="emit('dismiss')" />
    <p v-if="failed" role="status">Video se nepodařilo načíst.</p>
    <p v-else-if="autoplayBlocked" role="status">Prohlížeč zablokoval automatické přehrávání. Video spustíš jeho ovládáním.</p>
    <button type="button" @click="emit('dismiss')">Pokračovat</button>
  </section>
</template>

<style scoped>
.rickroll-video { display: grid; align-content: center; gap: 1.5rem; min-height: 100dvh; padding: max(1rem, env(safe-area-inset-top)) 1rem max(1rem, env(safe-area-inset-bottom)); background: #000; color: #fff; text-align: center; }
.rickroll-video h1 { margin: 0; font-size: clamp(1.25rem, 4vw, 2rem); }
.rickroll-video video { width: min(100%, 70rem); max-height: 75dvh; aspect-ratio: 16 / 9; justify-self: center; }
.rickroll-video button { justify-self: center; min-height: 44px; padding: .5rem 1rem; color: inherit; background: transparent; border: 1px solid currentColor; border-radius: .5rem; cursor: pointer; }
.rickroll-video button:focus-visible { outline: 2px solid currentColor; outline-offset: 4px; }
</style>
