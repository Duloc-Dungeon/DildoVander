<script setup lang="ts">
import GameCharacter from './GameCharacter.vue';

withDefaults(defineProps<{ intro?: boolean; phase?: number; expression?: number }>(), {
  intro: false,
  expression: 0,
});
</script>

<template>
  <div class="boss-theatre" :class="{ 'boss-theatre--intro': intro }">
    <div class="boss-theatre__lights" aria-hidden="true">
      <div class="boss-theatre__beam boss-theatre__beam--left" />
      <div class="boss-theatre__beam boss-theatre__beam--right" />
      <div class="boss-theatre__floor" />
    </div>
    <div class="boss-theatre__portrait">
      <GameCharacter class="boss-theatre__figure" character="boss" :expression="expression" />
      <div :key="phase" class="boss-theatre__veil" aria-hidden="true" />
    </div>
    <div class="boss-theatre__content"><slot /></div>
  </div>
</template>

<style scoped>
.boss-theatre {
  position: relative;
  isolation: isolate;
  display: grid;
  grid-template-columns: minmax(0, .8fr) minmax(0, 1.2fr);
  align-items: end;
  min-height: 210px;
  border-bottom: 1px solid var(--wns-line);
}
.boss-theatre__lights { position: absolute; inset: 0; z-index: -1; overflow: hidden; pointer-events: none; filter: blur(6px); }
.boss-theatre__beam {
  position: absolute;
  inset: -15% -15% 0;
  background: linear-gradient(180deg, transparent, color-mix(in srgb, var(--boss-light) 18%, transparent) 65%, transparent);
  clip-path: polygon(41% 0, 47% 0, 84% 100%, 0 100%);
  transform-origin: 44% 0;
  animation: boss-light-open 1.1s cubic-bezier(.16, 1, .3, 1);
}
.boss-theatre__beam--right { transform: scaleX(-1); opacity: .55; animation-name: boss-light-open-right; }
.boss-theatre__floor {
  position: absolute;
  inset: auto 0 -18px;
  height: 80px;
  background: radial-gradient(ellipse at center, color-mix(in srgb, var(--boss-light) 22%, transparent), transparent 68%);
}
.boss-theatre__portrait { position: relative; align-self: end; overflow: hidden; }
.boss-theatre :deep(.boss-theatre__figure) {
  width: 100%;
  max-width: 155px;
  margin-inline: auto;
  border: 0;
  border-radius: 0;
  mask-image: linear-gradient(#000 80%, transparent 100%);
}
.boss-theatre__veil {
  position: absolute;
  inset: 0;
  pointer-events: none;
  background: var(--wns-bg);
  clip-path: inset(0 100% 0 0);
  animation: boss-shadow-reveal 1.1s cubic-bezier(.16, 1, .3, 1);
}
.boss-theatre__content { position: relative; padding: 20px 0 24px 16px; }
.boss-theatre--intro { min-height: 360px; display: block; }
.boss-theatre--intro .boss-theatre__portrait { position: absolute; width: 66%; right: 0; bottom: 0; }
.boss-theatre--intro :deep(.boss-theatre__figure) { max-width: 260px; }
.boss-theatre--intro .boss-theatre__content { padding: 0 0 24px; min-height: 360px; display: flex; flex-direction: column; justify-content: end; }
.boss-theatre--intro .boss-theatre__content::before {
  content: '';
  position: absolute;
  z-index: -1;
  inset: 45% 0 0;
  background: linear-gradient(transparent, var(--wns-bg) 85%);
  pointer-events: none;
}
@keyframes boss-shadow-reveal { from { clip-path: inset(0); } to { clip-path: inset(0 100% 0 0); } }
@keyframes boss-light-open { from { opacity: 0; transform: rotate(-12deg); } to { opacity: 1; transform: rotate(0); } }
@keyframes boss-light-open-right { from { opacity: 0; transform: scaleX(-1) rotate(-12deg); } to { opacity: .55; transform: scaleX(-1) rotate(0); } }
@media (max-width: 360px) {
  .boss-theatre { grid-template-columns: 100px minmax(0, 1fr); min-height: 180px; }
  .boss-theatre--intro, .boss-theatre--intro .boss-theatre__content { min-height: 310px; }
  .boss-theatre--intro { display: block; }
}
@media (prefers-reduced-motion: reduce) {
  .boss-theatre__beam, .boss-theatre__veil { animation: none; }
}
</style>
