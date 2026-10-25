<script setup lang="ts">
import { ref, useId } from 'vue';
import AwButton from '../../components/AwButton.vue';
import { customerCopy } from '../../application/customerCopy';
import type { DuelPartyTask } from './showContracts';
defineProps<{ task: DuelPartyTask; loser: string; winner: string }>();
const choice = ref<'main' | 'alternative' | 'skip' | null>(null);
const headingId = useId();
</script>

<template>
  <section class="duel-party-task" :aria-labelledby="headingId">
    <h2 :id="headingId">{{ task.title }}</h2>
    <p class="duel-party-task__cast">{{ customerCopy.language.value === 'en' ? 'The unfortunate hero' : 'Hrdina s nešťastným výsledkem' }}: <strong>{{ loser }}</strong><template v-if="task.involvesWinner"> · {{ customerCopy.language.value === 'en' ? 'assisted by' : 'za přispění' }} <strong>{{ winner }}</strong></template>.</p>
    <p>{{ task.text }}</p>
    <details><summary>{{ customerCopy.language.value === 'en' ? 'What is the easier option?' : 'Co je lehčí varianta?' }}</summary><p>{{ task.alternative }}</p></details>
    <div class="duel-party-task__actions">
      <AwButton :aria-pressed="choice === 'main'" @click="choice = 'main'">{{ customerCopy.language.value === 'en' ? 'Challenge accepted' : 'Beru výzvu' }}</AwButton>
      <AwButton variant="secondary" :aria-pressed="choice === 'alternative'" @click="choice = 'alternative'">{{ customerCopy.language.value === 'en' ? 'Choose the easier option' : 'Zvolit lehčí variantu' }}</AwButton>
      <AwButton variant="secondary" :aria-pressed="choice === 'skip'" @click="choice = 'skip'">{{ customerCopy.language.value === 'en' ? 'Skip this one' : 'Dnes vynechám' }}</AwButton>
    </div>
    <div class="duel-party-task__reply" aria-live="polite" aria-atomic="true">
      <template v-if="choice === 'alternative'"><p>{{ task.alternativeRoast }}</p><p>{{ task.alternative }}</p></template>
      <p v-else-if="choice === 'main'">{{ customerCopy.language.value === 'en' ? 'Excellent. Olivander referred your courage to the committee. They are currently under the table together. The minutes will be approximate.' : 'Výborně. Olivander postoupil tvou odvahu příslušné komisi. Právě jsou spolu pod stolem. Zápis bude přibližný.' }}</p>
      <p v-else-if="choice === 'skip'">{{ customerCopy.language.value === 'en' ? 'Filed under Grand Intentions. Olivander has a lifetime subscription to that department.' : 'Odloženo do archivu Velkolepých záměrů. Olivander tam má celoživotní předplatné.' }}</p>
    </div>
    <p class="duel-party-task__note">{{ customerCopy.language.value === 'en' ? 'An optional party suggestion. Choose your drink, pace and movement. Nobody here audits courage or press-ups.' : 'Dobrovolná party výzva. Nápoj, tempo i pohyb si volíš sám. Nikdo tu nekontroluje hrdinství ani počet kliků.' }}</p>
  </section>
</template>

<style scoped>
.duel-party-task { margin-block: 2rem; padding-block: 1.5rem; border-block: 1px solid var(--aw-border-subtle); line-height: 1.75; overflow-wrap: anywhere; }
.duel-party-task h2 { font-family: var(--aw-font-display); font-weight: 500; font-size: clamp(2rem, 4vw, 3rem); }
.duel-party-task__cast, .duel-party-task__note { color: var(--aw-muted); font-size: .85rem; }
.duel-party-task__actions { display: flex; gap: .75rem; flex-wrap: wrap; margin-block: 1rem; }
.duel-party-task summary { cursor: pointer; min-height: 44px; padding-block: .5rem; color: var(--aw-link); }
.duel-party-task__reply { color: var(--aw-ivory); }
.duel-party-task__actions [aria-pressed='true'] { outline: 2px solid var(--aw-info); outline-offset: 3px; }
@media (max-width: 480px) { .duel-party-task__actions { display: grid; } }
</style>
