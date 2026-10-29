<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import WnsWordmark from '../components/WnsWordmark.vue';
import { customerCopy } from './customerCopy';

const emit = defineEmits<{ resize: [height: number] }>();
const header = ref<HTMLElement>();
let observer: ResizeObserver | undefined;
onMounted(() => {
  const measure = () => emit('resize', header.value?.getBoundingClientRect().height ?? 0);
  measure();
  if (typeof ResizeObserver !== 'undefined' && header.value) {
    observer = new ResizeObserver(measure);
    observer.observe(header.value, { box: 'border-box' });
  }
});
onBeforeUnmount(() => observer?.disconnect());
</script>

<template>
  <header ref="header" class="application-topbar">
    <div class="application-topbar__content">
      <RouterLink class="application-topbar__brand" to="/" :aria-label="customerCopy.text('shell.brand-home')"><WnsWordmark /></RouterLink>
      <div id="application-header-actions" class="application-topbar__actions"><slot /></div>
    </div>
  </header>
</template>

<style scoped>
.application-topbar {
  position: sticky;
  top: 0;
  z-index: 25;
  padding-top: var(--safe-area-inset-top, env(safe-area-inset-top, 0px));
  border-bottom: 1px solid var(--wns-line);
  background: var(--wns-bg);
  color: var(--wns-text);
}
.application-topbar__content {
  width: min(100%, 64rem);
  margin-inline: auto;
  padding: var(--wns-space-12) max(clamp(16px, 4vw, 32px), var(--safe-area-inset-right, env(safe-area-inset-right, 0px))) var(--wns-space-12) max(clamp(16px, 4vw, 32px), var(--safe-area-inset-left, env(safe-area-inset-left, 0px)));
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: var(--wns-space-12) var(--wns-space-24);
}
.application-topbar__brand { display: inline-flex; flex: none; min-height: 44px; color: inherit; text-decoration: none; }
.application-topbar__actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: var(--wns-space-12); min-width: 0; max-width: 100%; }
.application-topbar__actions:empty { display: none; }
.application-topbar__actions :deep(.aw-button) { min-height: 44px; border-radius: var(--wns-radius-field); background: var(--wns-surface-raised); color: var(--wns-text); border-color: var(--wns-line); }
.application-topbar__actions :deep(.aw-button--danger) { background: var(--wns-peach); color: var(--wns-on-peach); }
.application-topbar :deep(:focus-visible) { outline: 3px solid var(--wns-focus-ring); outline-offset: 4px; }
</style>
