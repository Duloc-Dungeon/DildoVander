<script setup lang="ts">
import spark from '../../assets/arcade/spark.webp';
import seal from '../../assets/wns-v2/art/seal-core.png';
import wand from '../../assets/wns-v2/art/wand-amber.png';
import arrow from '../../assets/wns-v2/icons/regular/arrow-right.svg';

withDefaults(defineProps<{ includeArcade?: boolean }>(), { includeArcade: true });
</script>

<template>
  <nav class="game-destinations" aria-label="Samostatné hry">
    <RouterLink class="game-destination game-destination--duels" to="/play/souboj-hulek" aria-labelledby="duels-entry-title" aria-describedby="duels-entry-description">
      <div class="game-destination__copy">
        <h2 id="duels-entry-title">Souboj hůlek</h2>
        <p class="game-destination__kind">1v1 · YOLO · Týmová hra</p>
        <p id="duels-entry-description">Vyzvi soupeře, zkus příběhovou výzvu nebo spoj síly ve dvojici.</p>
        <span class="game-destination__action">Vybrat režim <img :src="arrow" alt="" width="20" height="20" /></span>
      </div>
      <img class="game-destination__art" :src="wand" alt="" width="128" height="128" />
    </RouterLink>
    <RouterLink v-if="includeArcade" class="game-destination game-destination--spark" to="/play/kouzlo-bez-dozoru" aria-labelledby="spark-entry-title" aria-describedby="spark-entry-description">
      <div class="game-destination__copy">
        <h2 id="spark-entry-title">Kouzlo bez dozoru</h2>
        <p class="game-destination__kind">Arkáda pro jednoho</p>
        <p id="spark-entry-description">Veď jiskru skladem a překonej rekord skupiny.</p>
        <span class="game-destination__action">Hrát <img :src="arrow" alt="" width="20" height="20" /></span>
      </div>
      <img class="game-destination__art game-destination__spark" :src="spark" alt="" width="128" height="128" />
    </RouterLink>
    <RouterLink class="game-destination game-destination--sealed" to="/play/pod-peceti" aria-labelledby="sealed-entry-title" aria-describedby="sealed-entry-description">
      <div class="game-destination__copy">
        <h2 id="sealed-entry-title">Pod pečetí</h2>
        <p class="game-destination__kind">Příběhová hra pro skupinu</p>
        <p id="sealed-entry-description">Společný případ. Tajné role, stopy a vlastní plány.</p>
        <span class="game-destination__action">Vstoupit do hry <img :src="arrow" alt="" width="20" height="20" /></span>
      </div>
      <img class="game-destination__art" :src="seal" alt="" width="128" height="128" />
    </RouterLink>
  </nav>
</template>

<style scoped>
.game-destinations { display: grid; gap: 16px; margin-block: 24px 32px; }
.game-destination {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 96px;
  align-items: center;
  gap: 12px;
  padding: 20px;
  border: 1px solid var(--wns-line);
  border-radius: 16px;
  background: var(--wns-surface);
  color: var(--wns-text);
  text-decoration: none;
  transition: border-color 160ms ease-out, background-color 160ms ease-out;
}
.game-destination--spark { --entry-accent: var(--wns-mint); }
.game-destination--duels { --entry-accent: var(--wns-amber); }
.game-destination--sealed { --entry-accent: var(--wns-amber); }
.game-destinations .game-destination h2 { margin: 0; font-size: 1.35rem; line-height: 1.2; letter-spacing: -.025em; text-wrap: balance; }
.game-destinations .game-destination p { margin: 8px 0 0; font-size: .875rem; line-height: 1.5; color: var(--wns-muted); }
.game-destinations .game-destination .game-destination__kind { margin-top: 6px; color: var(--entry-accent); font-weight: 650; }
.game-destination__art { width: 100%; height: auto; object-fit: contain; }
.game-destination__spark { transform: scale(1.2); }
.game-destination__action { display: inline-flex; align-items: center; gap: 10px; margin-top: 16px; font-size: .9375rem; font-weight: 760; color: var(--wns-text); }
.game-destination__action img { filter: invert(1); }
.game-destination:hover { border-color: var(--entry-accent); background: var(--wns-surface-raised); }
.game-destination:hover .game-destination__action { text-decoration: underline; text-underline-offset: 4px; }
.game-destination:focus-visible { outline: 3px solid var(--wns-focus-ring); outline-offset: 4px; }
.game-destination:active { background: var(--wns-surface-raised); }
@media (max-width: 360px) {
  .game-destination { grid-template-columns: minmax(0, 1fr) 64px; gap: 8px; padding: 16px; }
}
@media (prefers-reduced-motion: reduce) { .game-destination { transition: none; } }
</style>
