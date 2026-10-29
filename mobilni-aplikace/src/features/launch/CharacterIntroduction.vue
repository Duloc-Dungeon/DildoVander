<script setup lang="ts">
defineOptions({ inheritAttrs: false });
defineProps<{ text: string; active?: boolean; replayKey?: number }>();
</script>

<template>
  <Transition name="character-line" mode="out-in" appear>
    <p :key="`${replayKey ?? 0}:${text}`" v-bind="$attrs" class="character-introduction" :class="{ 'character-introduction-paused': active === false }">{{ text }}</p>
  </Transition>
</template>

<style scoped>
.character-line-enter-active { animation: character-line-arrive .48s cubic-bezier(.16, 1, .3, 1) both; }
.character-line-leave-active { transition: opacity .18s ease-out; }
.character-line-leave-to { opacity: 0; }
.character-introduction-paused { animation-play-state: paused; }
@keyframes character-line-arrive { from { opacity: .5; transform: translateY(8px); filter: blur(1px); } to { opacity: 1; transform: translateY(0); filter: blur(0); } }
@media (prefers-reduced-motion: reduce), (update: slow) { .character-line-enter-active { animation: none; } .character-line-leave-active { transition: none; } }
</style>
