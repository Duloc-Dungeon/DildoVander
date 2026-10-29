<script setup lang="ts">
import checkIcon from '../../assets/wns-v2/icons/regular/check.svg';
</script>

<template>
  <div class="admin-confirmation" role="status">
    <span class="admin-confirmation__seal" aria-hidden="true"><img :src="checkIcon" alt="" width="22" height="22" /></span>
    <p><slot /></p>
  </div>
</template>

<style scoped>
.admin-confirmation { display: flex; align-items: center; gap: .85rem; padding: 1rem; border-radius: var(--wns-radius-field); background: var(--aw-accent-soft); color: var(--aw-accent); overflow-wrap: anywhere; }
.admin-confirmation p { margin: 0; min-width: 0; }
.admin-confirmation__seal { display: grid; place-items: center; width: 36px; height: 36px; flex: 0 0 36px; border-radius: 50%; background: var(--wns-mint); }
.admin-confirmation__seal img { animation: confirmation-mark 240ms cubic-bezier(.16, 1, .3, 1); }
@keyframes confirmation-mark { from { clip-path: inset(0 100% 0 0); } to { clip-path: inset(0); } }
@media (prefers-reduced-motion: reduce) { .admin-confirmation__seal img { animation: none; } }
</style>
