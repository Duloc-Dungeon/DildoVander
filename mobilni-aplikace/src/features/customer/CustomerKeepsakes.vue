<script setup lang="ts">
import { reactive } from 'vue';
import caretDown from '../../assets/wns-v2/icons/regular/caret-down.svg';
import AccountArchiveList from '../identity/AccountArchiveList.vue';
import CustomerCompletionUpdates from './CustomerCompletionUpdates.vue';
import CustomerDuelHistory from './CustomerDuelHistory.vue';
import CustomerGameTitles from './CustomerGameTitles.vue';
import CustomerSealedCaseRewards from './CustomerSealedCaseRewards.vue';

defineProps<{ locale?: 'cs' | 'en' }>();

type SectionKey = 'updates' | 'titles' | 'cases' | 'battles';
const expanded = reactive<Record<SectionKey, boolean>>({
  updates: false,
  titles: false,
  cases: false,
  battles: false,
});

function syncExpanded(key: SectionKey, event: Event): void {
  expanded[key] = (event.currentTarget as HTMLDetailsElement).open;
}
</script>

<template>
  <section class="customer-keepsakes" aria-labelledby="customer-keepsakes-secondary-title">
    <AccountArchiveList :locale="locale" />

    <div class="customer-keepsakes__secondary">
      <h2 id="customer-keepsakes-secondary-title">Další záznamy</h2>

      <details class="customer-keepsakes__disclosure" @toggle="syncExpanded('updates', $event)">
        <summary>Aktualizace výsledků<img :src="caretDown" alt="" width="20" height="20" /></summary>
        <CustomerCompletionUpdates v-if="expanded.updates" :locale="locale" />
      </details>

      <details class="customer-keepsakes__disclosure" @toggle="syncExpanded('titles', $event)">
        <summary>Herní tituly<img :src="caretDown" alt="" width="20" height="20" /></summary>
        <CustomerGameTitles v-if="expanded.titles" :locale="locale" />
      </details>

      <details class="customer-keepsakes__disclosure" @toggle="syncExpanded('cases', $event)">
        <summary>Památky z uzavřených případů<img :src="caretDown" alt="" width="20" height="20" /></summary>
        <CustomerSealedCaseRewards v-if="expanded.cases" :locale="locale" />
      </details>

      <details class="customer-keepsakes__disclosure" @toggle="syncExpanded('battles', $event)">
        <summary>Uložené souboje<img :src="caretDown" alt="" width="20" height="20" /></summary>
        <CustomerDuelHistory v-if="expanded.battles" />
      </details>
    </div>
  </section>
</template>

<style scoped>
.customer-keepsakes { display: grid; gap: 2rem; min-width: 0; }
.customer-keepsakes__secondary { display: grid; gap: .25rem; min-width: 0; }
.customer-keepsakes__secondary > h2 { margin: 0 0 .75rem; font: 750 1.2rem/1.3 var(--wns-font-family, sans-serif); }
.customer-keepsakes__disclosure { border-top: 1px solid var(--wns-line, #343939); }
.customer-keepsakes__disclosure:last-child { border-bottom: 1px solid var(--wns-line, #343939); }
.customer-keepsakes__disclosure > summary { min-height: 48px; display: flex; align-items: center; justify-content: space-between; gap: 1rem; padding-block: .5rem; cursor: pointer; color: var(--wns-text, #f5f5f0); font-weight: 700; }
.customer-keepsakes__disclosure > summary img { flex: none; filter: invert(1); }
.customer-keepsakes__disclosure[open] > summary img { transform: rotate(180deg); }
.customer-keepsakes__disclosure > :deep(section) { margin-block: .75rem 1rem; }
.customer-keepsakes__disclosure :deep(.aw-card) { box-shadow: none; }
.customer-keepsakes :deep(:focus-visible) { outline: 2px solid var(--wns-mint, #b8f3d4); outline-offset: 4px; }
</style>
