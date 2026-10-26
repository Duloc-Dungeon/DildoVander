<script setup lang="ts">
import tabletop from '../../assets/brand/midnight/tabletop.webp';

withDefaults(defineProps<{ background?: string; labelledBy?: string; active?: boolean }>(), {
  background: tabletop,
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
.scene-shell { position: relative; isolation: isolate; min-height: 100dvh; color: var(--aw-game-paper, var(--aw-ivory)); background-color: var(--aw-game-felt, var(--aw-void)); background-image: linear-gradient(180deg, rgb(4 15 14 / .18), rgb(4 15 14 / .56)), var(--scene-background); background-position: center; background-size: cover; }
.scene-foreground { position: absolute; inset: 0; z-index: 0; overflow: hidden; pointer-events: none; }
.scene-shell :deep([data-scene-enter]) { animation: scene-enter .55s ease-out both; }
.scene-shell--paused :deep([data-scene-enter]) { animation-play-state: paused; }
@keyframes scene-enter { from { opacity: 0; transform: translateY(.5rem); } to { opacity: 1; transform: translateY(0); } }
@media (prefers-reduced-motion: reduce) { .scene-shell { scroll-behavior: auto; } .scene-shell :deep([data-scene-enter]) { animation: none; } }
</style>
