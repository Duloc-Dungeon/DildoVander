<script setup lang="ts">
import { computed, useId, watch } from 'vue';
import type { InterfaceLanguagePreference } from './interfaceLanguage';

const props = defineProps<{
  preference: InterfaceLanguagePreference;
  accountId?: string | null;
}>();
const hintId = `interface-language-${useId()}-scope`;

watch(
  () => [props.preference, props.accountId] as const,
  ([preference, accountId]) => preference.setScope(accountId),
  { immediate: true, flush: 'sync' },
);

const scopeHint = computed(() => {
  const scope = props.preference.scope.value;
  const persistence = props.preference.persistenceStatus.value;
  if (persistence === 'unavailable') {
    return 'Volba platí jen v tomto spuštění; prohlížeč ji nemůže uložit.';
  }
  if (persistence === 'unverified') {
    return scope.kind === 'account'
      ? 'Pro tento účet v tomto zařízení; trvalé uložení zatím není ověřené.'
      : 'Pro toto zařízení; trvalé uložení zatím není ověřené.';
  }
  return scope.kind === 'account'
    ? 'Uloženo pouze pro tento účet v tomto zařízení.'
    : 'Uloženo pouze na tomto zařízení.';
});
</script>

<template>
  <div class="interface-language-select">
    <span class="interface-language-select__label">Jazyk rozhraní</span>
    <span class="interface-language-select__value" :aria-describedby="hintId">Čeština</span>
    <small :id="hintId">{{ scopeHint }}</small>
  </div>
</template>

<style scoped>
.interface-language-select { display: grid; gap: var(--aw-space-2); min-width: 0; width: clamp(9rem, 20vw, 22rem); max-width: 100%; overflow-wrap: anywhere; }
.interface-language-select__label { font-size: var(--aw-text-small); }
.interface-language-select__value { width: 100%; min-width: 0; min-height: var(--aw-touch); padding: var(--aw-space-2) var(--aw-space-3); border: 1px solid var(--aw-border); border-radius: var(--aw-radius-control); background: var(--aw-void); color: var(--aw-ivory); font-size: 1rem; display: flex; align-items: center; }
.interface-language-select small { color: var(--aw-muted); font-size: var(--aw-text-small); }
</style>
