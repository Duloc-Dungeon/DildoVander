<script setup lang="ts">
import commentator from '../../assets/brand/game/commentator.webp';
import boss from '../../assets/brand/game/boss.webp';
withDefaults(defineProps<{ character: 'commentator' | 'boss'; expression?: number }>(), { expression: 0 });
</script>
<template>
  <div class="game-character" :style="{ backgroundImage: `url(${character === 'boss' ? boss : commentator})`, backgroundPosition: `${Math.min(2, Math.max(0, expression)) * 50}% 0%` }" role="img" :aria-label="character === 'boss' ? 'Baron Šarlatán' : 'Bohumil Křivopísk, rozhodčí bez licence'" />
</template>
<style scoped>
.game-character { width: 100%; aspect-ratio: 1 / 1.58; background-size: 300% auto; background-repeat: no-repeat; border-radius: 50% 50% 10px 10px / 20% 20% 10px 10px; border: 1px solid var(--aw-game-brass); }
</style>
