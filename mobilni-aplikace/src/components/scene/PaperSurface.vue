<script setup lang="ts">
import paper from '../../assets/brand/midnight/paper.webp';

withDefaults(defineProps<{ as?: string }>(), { as: 'section' });
</script>

<template>
  <component :is="as" class="paper-surface" :style="{ '--paper-texture': `url(${paper})` }"><slot /></component>
</template>

<style scoped>
.paper-surface { --aw-void: var(--aw-game-paper, #efe0c3); --aw-ivory: var(--aw-game-ink, #2a2019); --aw-muted: var(--aw-game-muted-ink, #625340); --aw-surface: #f5e8cc; --aw-elevated: #e9d6b4; --aw-border: #8b6c45; --aw-border-subtle: #b6986f; --aw-link: #513c26; --aw-danger: #9b2637; --aw-warning: #725018; --aw-info: #265951; --aw-success: #285d38; --aw-accent-soft: #dfc79f; color: var(--aw-game-ink, #2a2019); background-color: var(--aw-game-paper, #efe0c3); background-image: var(--paper-texture, none); border: 1px solid var(--aw-game-brass, #d6a45f); border-radius: 8px; box-shadow: 0 1rem 2.5rem rgb(0 0 0 / .28), inset 0 0 0 4px rgb(114 73 32 / .08); padding: clamp(1rem, 4vw, 2rem); }
.paper-surface :deep(a) { color: var(--aw-game-ink, #2a2019); text-decoration-color: var(--aw-game-brass, #8d6228); text-underline-offset: .2em; }
.paper-surface :deep(.aw-notice) { color: var(--aw-game-ink, #2a2019); background: rgb(255 255 255 / .4); border-color: var(--aw-game-brass, #d6a45f); }
.paper-surface :deep(.aw-notice strong) { color: var(--aw-game-ink, #2a2019); }
.paper-surface :deep(.aw-notice--danger) { background: #f8e2df; border-color: #9b2637; }
.paper-surface :deep(.aw-notice--warning) { background: #f4e9c8; border-color: #725018; }
.paper-surface :deep(.aw-notice--info) { background: #e1ece4; border-color: #265951; }
.paper-surface :deep(.aw-notice--success) { background: #e4efdf; border-color: #285d38; }
.paper-surface :deep(.aw-button--secondary) { color: var(--aw-game-ink, #2a2019); background: transparent; border-color: var(--aw-border, #8b6c45); }
.paper-surface :deep(button:focus-visible), .paper-surface :deep(input:focus-visible), .paper-surface :deep(select:focus-visible), .paper-surface :deep(summary:focus-visible), .paper-surface :deep(a:focus-visible) { outline: 3px solid var(--aw-game-ink, #2a2019); outline-offset: 3px; box-shadow: 0 0 0 1px var(--aw-game-paper, #efe0c3); }
</style>
