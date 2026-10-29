<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { Capacitor, type PluginListenerHandle } from '@capacitor/core';
import AwButton from '../components/AwButton.vue';
import type { ApplicationUpdates } from './applicationUpdates';

const { updates } = defineProps<{ updates: ApplicationUpdates }>();
const heading = ref<HTMLElement>();
let listener: PluginListenerHandle | undefined;
let disposed = false;
let interval: ReturnType<typeof setInterval> | undefined;
function refresh(): void {
  if (!document.hidden) void updates.check();
}
watch(updates.blocked, async blocked => {
  if (blocked) { await nextTick(); heading.value?.focus(); }
});
onMounted(() => {
  heading.value?.focus();
  refresh();
  if (!Capacitor.isNativePlatform()) return;
  document.addEventListener('visibilitychange', refresh);
  window.addEventListener('online', refresh);
  interval = setInterval(refresh, 5 * 60 * 1000);
  void import('@capacitor/app').then(async ({ App }) => {
    const handle = await App.addListener('appStateChange', ({ isActive }) => { if (isActive) refresh(); });
    if (disposed) await handle.remove();
    else listener = handle;
  }).catch(() => { /* Visibility/online checks remain available if the listener fails. */ });
});
onBeforeUnmount(() => {
  disposed = true;
  clearInterval(interval);
  document.removeEventListener('visibilitychange', refresh);
  window.removeEventListener('online', refresh);
  void listener?.remove();
});
</script>

<template>
  <main v-if="updates.blocked.value" id="main-content" class="application-update-gate">
    <section class="application-update-card" aria-labelledby="application-update-title" :aria-busy="updates.checking.value">
      <h1 id="application-update-title" ref="heading" tabindex="-1">{{ updates.required.value ? 'Je čas na aktualizaci' : updates.checking.value ? 'Kontrolujeme verzi aplikace' : 'Ověřte verzi aplikace' }}</h1>
      <p v-if="updates.required.value">Tato verze už není podporovaná. Pro pokračování si nainstalujte aktualizaci a znovu otevřete aplikaci.</p>
      <p v-else-if="updates.checking.value" role="status">Chvilku strpení, ověřujeme dostupné aktualizace.</p>
      <p v-if="updates.installed.value" class="application-update-version">Nainstalováno: {{ updates.installed.value.version }} ({{ updates.installed.value.build }})</p>
      <p v-if="updates.error.value" role="alert">{{ updates.error.value }}</p>
      <div class="application-update-actions">
        <a v-if="updates.required.value && updates.policy.value?.updateUrl" class="aw-button aw-button--primary" :href="updates.policy.value.updateUrl" target="_blank" rel="noopener noreferrer">Aktualizovat aplikaci</a>
        <AwButton :busy="updates.checking.value" @click="updates.check">Zkontrolovat znovu</AwButton>
      </div>
    </section>
  </main>
  <template v-else>
    <aside v-if="updates.available.value" class="application-update-banner" aria-label="Aktualizace aplikace">
      <span>Je dostupná nová verze aplikace.</span>
      <a :href="updates.policy.value!.updateUrl" target="_blank" rel="noopener noreferrer">Aktualizovat</a>
    </aside>
    <slot />
  </template>
</template>

<style scoped>
.application-update-gate { min-height: var(--application-content-height, 100svh); display: grid; place-items: center; padding: 2rem max(1.25rem, env(safe-area-inset-right)) max(2rem, env(safe-area-inset-bottom)) max(1.25rem, env(safe-area-inset-left)); }
.application-update-card { width: min(100%, 32rem); padding: clamp(1.25rem, 5vw, 2.5rem); border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-card); background: var(--aw-surface); overflow-wrap: anywhere; }
h1 { font-size: clamp(1.75rem, 6vw, 2.5rem); line-height: 1.15; }
.application-update-gate h1:focus { outline: none; }
p { line-height: 1.6; }
.application-update-version { color: var(--aw-muted); font-size: .875rem; }
.application-update-actions { display: flex; flex-wrap: wrap; gap: .75rem; margin-top: 1.5rem; }
.application-update-actions > * { min-height: 44px; text-align: center; }
.application-update-banner { display: flex; flex-wrap: wrap; justify-content: center; align-items: center; gap: .5rem 1rem; padding: .75rem 1rem; background: var(--aw-surface); border-bottom: 1px solid var(--aw-border-subtle); }
.application-update-banner a { display: inline-flex; align-items: center; min-height: 44px; }
</style>
