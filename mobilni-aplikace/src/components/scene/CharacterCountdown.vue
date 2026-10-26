<script setup lang="ts">
defineProps<{ name: string; universe: string; values: readonly (string | number)[]; units: readonly string[]; title: string; label: string; headingId?: string; headingAs?: string; unscheduled?: string }>();
</script>

<template>
  <section class="character-countdown" :aria-label="label">
    <header class="character-countdown-identity"><component :is="headingAs || 'h1'" :id="headingId">{{ name }}</component><p>{{ universe }}</p></header>
    <p class="character-countdown-title">{{ title }}</p>
    <div v-if="!unscheduled" class="character-countdown-grid" aria-live="off">
      <div v-for="(value, index) in values" :key="index"><strong>{{ String(value).padStart(2, '0') }}</strong><span>{{ units[index] }}</span></div>
    </div>
    <p v-else class="character-countdown-wait">{{ unscheduled }}</p>
  </section>
</template>

<style scoped>
.character-countdown { min-width: 0; text-align: center; }
.character-countdown-identity :deep(h1), .character-countdown-identity :deep(h2), .character-countdown-identity :deep(h3) { margin: 0; color: inherit; font: 500 clamp(1.5rem, 5vw, 2.5rem)/1.1 var(--aw-font-display); overflow-wrap: anywhere; }
.character-countdown-identity p { margin: .35rem 0 1rem; color: inherit; overflow-wrap: anywhere; }
.character-countdown-title { margin: 0 0 .35rem; font-size: .9rem; }
.character-countdown-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: .35rem; }
.character-countdown-grid > div { display: grid; gap: .15rem; min-width: 0; }
.character-countdown-grid strong { font-family: var(--aw-font-display); font-size: clamp(2rem, 9vw, 2.8rem); font-variant-numeric: tabular-nums; font-weight: 500; line-height: 1.1; }
.character-countdown-grid span, .character-countdown-wait { font-size: clamp(.68rem, 2.8vw, .88rem); }
.character-countdown-wait { margin: .5rem 0 0; }
</style>
