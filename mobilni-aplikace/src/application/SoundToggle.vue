<script setup lang="ts">
import { wnsSound } from './wnsSound';

function toggleSound(): void {
  wnsSound.enable(!wnsSound.enabled.value);
}
</script>

<template>
  <button
    type="button"
    class="sound-toggle"
    :class="{ 'sound-toggle--enabled': wnsSound.enabled.value }"
    :aria-pressed="wnsSound.enabled.value"
    :aria-label="wnsSound.enabled.value ? 'Zvuk zapnutý, vypnout' : 'Zvuk vypnutý, zapnout'"
    @click="toggleSound"
  >
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M4 10v4h4l5 4V6l-5 4H4Z" />
      <path v-if="wnsSound.enabled.value" d="M16 9a5 5 0 0 1 0 6m2-9a9 9 0 0 1 0 12" />
      <path v-else d="m16 9 5 6m0-6-5 6" />
    </svg>
    <span>Zvuk</span>
    <span class="sound-toggle__state">{{ wnsSound.enabled.value ? 'Zapnuto' : 'Vypnuto' }}</span>
  </button>
</template>

<style scoped>
.sound-toggle { display: inline-flex; min-width: 44px; min-height: 44px; align-items: center; justify-content: center; gap: .55rem; padding: .55rem .85rem; border: 1px solid var(--aw-border-subtle); border-radius: 999px; background: var(--aw-surface); color: var(--aw-ivory); font: inherit; font-weight: 650; cursor: pointer; }
.sound-toggle--enabled { border-color: var(--aw-accent); background: var(--aw-accent-soft); }
.sound-toggle svg { width: 1.25rem; height: 1.25rem; fill: none; stroke: currentColor; stroke-linecap: round; stroke-linejoin: round; stroke-width: 1.8; }
.sound-toggle__state { padding: .12rem .45rem; border-radius: 999px; background: var(--aw-elevated); font-size: .75rem; }
.sound-toggle:focus-visible { outline: 3px solid var(--aw-accent); outline-offset: 2px; }
</style>
