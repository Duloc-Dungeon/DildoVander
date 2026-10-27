<script setup lang="ts">
withDefaults(defineProps<{ as?: string }>(), { as: 'section' });
</script>

<template>
  <component :is="as" class="paper-surface"><slot /></component>
</template>

<style scoped>
.paper-surface { --aw-void: var(--wns-bg, #080909); --aw-ivory: var(--wns-text, #f5f5f0); --aw-muted: var(--wns-muted, #b2b9b6); --aw-surface: var(--wns-surface, #191b1b); --aw-elevated: var(--wns-surface-raised, #222626); --aw-border: var(--wns-line, #343939); --aw-border-subtle: var(--wns-line, #343939); --aw-link: var(--wns-text, #f5f5f0); --aw-danger: var(--wns-danger, #f3a59b); --aw-accent: var(--wns-mint, #b8f3d4); --aw-on-accent: #111513; color: var(--wns-text, #f5f5f0); background: var(--wns-surface, #191b1b); border: 1px solid var(--wns-line, #343939); border-radius: 24px; box-shadow: none; padding: clamp(1rem, 4vw, 1.5rem); }
.paper-surface :deep(a) { color: var(--wns-text, #f5f5f0); text-underline-offset: .2em; }
.paper-surface :deep(.aw-notice) { color: var(--wns-text, #f5f5f0); background: var(--wns-surface-raised, #222626); border-color: var(--wns-line, #343939); }
.paper-surface :deep(.aw-notice--danger) { border-color: var(--wns-danger, #f3a59b); }
.paper-surface :deep(.aw-button--secondary) { color: var(--wns-text, #f5f5f0); background: transparent; border-color: var(--wns-line, #343939); }
.paper-surface :deep(button:focus-visible), .paper-surface :deep(input:focus-visible), .paper-surface :deep(select:focus-visible), .paper-surface :deep(summary:focus-visible), .paper-surface :deep(a:focus-visible) { outline: 2px solid var(--wns-mint, #b8f3d4); outline-offset: 4px; }
</style>
