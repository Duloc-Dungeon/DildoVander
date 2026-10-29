<script setup lang="ts">
import { computed } from 'vue';

const props = defineProps<{ playerEnergy?: number; opponentEnergy?: number; exhibition?: boolean }>();
const energies = computed(() => [
  { label: 'Tvoje energie', value: props.playerEnergy, max: 6 },
  { label: 'Baronova energie', value: props.opponentEnergy, max: 5 },
]);
const format = (value?: number) => value?.toLocaleString('cs-CZ', { maximumFractionDigits: 2 }) ?? '—';
const fraction = (value: number, max: number) => Math.max(0, Math.min(1, value / max));
</script>

<template>
  <div class="boss-energies" aria-label="Zbývající bojová energie">
    <div v-for="energy in energies" :key="energy.label" class="boss-energy">
      <span>{{ energy.label }}</span><strong>{{ format(energy.value) }}<span class="boss-energy__maximum"> / {{ energy.max }}</span></strong>
      <div v-if="energy.value !== undefined" class="boss-energy__track" role="meter"
        :aria-label="energy.label" :aria-valuenow="energy.value" :aria-valuemin="0" :aria-valuemax="energy.max">
        <span :style="{ transform: `scaleX(${fraction(energy.value, energy.max)})` }" />
      </div>
    </div>
  </div>
</template>

<style scoped>
.boss-energies { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 8rem), 1fr)); gap: var(--wns-space-24); margin-block: var(--wns-space-16); }
.boss-energy { display: grid; grid-template-columns: 1fr; align-content: start; gap: 6px; font-size: .875rem; }
.boss-energy > span { color: var(--wns-muted); line-height: 1.4; }
.boss-energy strong { font-size: 1.25rem; line-height: 1.2; font-weight: var(--wns-weight-heading); font-variant-numeric: tabular-nums; white-space: nowrap; }
.boss-energy__maximum { color: var(--wns-muted); font-size: .875rem; font-weight: var(--wns-weight-body); }
.boss-energy__track { grid-column: 1 / -1; height: 4px; background: var(--wns-line); overflow: hidden; }
.boss-energy__track span { display: block; height: 100%; background: var(--wns-mint); transform-origin: left; transition: transform .35s cubic-bezier(.16, 1, .3, 1); }
.boss-energy:last-child .boss-energy__track span { background: var(--boss-light); }
@media (max-width: 360px) { .boss-energies { gap: var(--wns-space-16); } }
@media (prefers-reduced-motion: reduce) { .boss-energy__track span { transition: none; } }
</style>
