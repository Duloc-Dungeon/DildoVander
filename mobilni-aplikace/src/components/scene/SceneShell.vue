<script setup lang="ts">
withDefaults(defineProps<{ background?: string; labelledBy?: string; active?: boolean }>(), {
  background: undefined,
  labelledBy: undefined,
  active: true,
});
</script>

<template>
  <section class="scene-shell" :class="{ 'scene-shell--paused': !active }" :aria-labelledby="labelledBy" :style="background ? { '--scene-background': `url(${background})` } : undefined">
    <slot />
    <div class="scene-foreground" aria-hidden="true"><slot name="foreground" /></div>
  </section>
</template>

<style scoped>
.scene-shell { position: relative; isolation: isolate; min-height: var(--application-content-height, 100dvh); color: var(--wns-text, #f5f5f0); background-color: var(--wns-bg, #080909); background-image: none; }
.scene-foreground { position: absolute; inset: 0; z-index: 0; overflow: hidden; pointer-events: none; }
.scene-shell :deep([data-scene-enter]) { animation: scene-enter .55s ease-out both; }
.scene-shell--paused :deep([data-scene-enter]) { animation-play-state: paused; }
@keyframes scene-enter { from { opacity: 0; transform: translateY(.5rem); } to { opacity: 1; transform: translateY(0); } }
@media (prefers-reduced-motion: reduce) { .scene-shell { scroll-behavior: auto; } .scene-shell :deep([data-scene-enter]) { animation: none; } }
</style>
