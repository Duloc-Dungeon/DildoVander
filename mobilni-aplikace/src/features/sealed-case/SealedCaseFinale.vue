<script setup lang="ts">
import { computed } from 'vue';
import AwButton from '../../components/AwButton.vue';
import { intentText, reasonText, resultText, scenarioFor, type Play } from './sealedCase';

const props = defineProps<{
  view: Play;
  medallion: { name: string; team: string; text: string } | null;
}>();
defineEmits<{ export: [personal: boolean]; 'new-case': [] }>();
const playerName = (id: string) => props.view.players.find(player => player.id === id)?.name ?? 'Host';
const scenario = computed(() => scenarioFor(props.view));
const actionText = (value: string) => value === 'Lethal' ? scenario.value.extremeActionLabel
  : props.view.chapters === 3 && value === 'Guard' ? 'Chránit sebe'
    : props.view.chapters === 3 && value === 'Sabotage' ? 'Zdržet stopu' : intentText(value);
</script>

<template>
  <section class="sealed-ending" aria-labelledby="sealed-winner">
    <p class="ending-context">{{ scenario.title }} · případ je uzavřený.</p>
    <h2 id="sealed-winner">Vítězí <span>{{ view.finale?.winner }}.</span></h2>
    <p class="ending-line">{{ view.finale?.message ?? 'Hlasování skončilo. Výsledek a průběh případu jsou odhalené.' }}</p>
    <dl class="ending-reveal">
      <div><dt>{{ scenario.conspiratorLabel }}</dt><dd>{{ view.finale?.conspirators?.map(playerName).join(', ') || 'Žádní odhalení hráči této strany' }}</dd></div>
      <div><dt>Označení hráči</dt><dd>{{ view.finale?.selected?.map(playerName).join(', ') || 'Nikdo nebyl označen' }}</dd></div>
    </dl>

    <section v-if="medallion" class="ending-memory" aria-labelledby="sealed-memory-title">
      <h3 id="sealed-memory-title">Tvá stopa v případu</h3>
      <p><strong>{{ medallion.name }}</strong> · {{ medallion.team }}</p>
      <p>{{ medallion.text }}</p>
    </section>

    <details class="ending-reconstruction">
      <summary>Jak se případ odehrál</summary>
      <ol v-if="view.finale?.reconstruction?.length">
        <li v-for="(entry, index) in view.finale.reconstruction" :key="index">
          <p class="entry-chapter">Kapitola {{ entry.chapter }} · {{ playerName(entry.actor) }}</p>
          <p><strong>{{ actionText(entry.intent) }}</strong> · {{ resultText(entry.result) }}</p>
          <p v-if="entry.reason">{{ reasonText(entry.reason) }}</p>
          <details v-if="entry.rolls" class="ending-rolls">
            <summary>Průběh hodu</summary>
            <p>Útok {{ entry.rolls.attack }}, obrana {{ entry.rolls.defense }}. Celkové součty {{ entry.rolls.attackTotal }} : {{ entry.rolls.defenseTotal }}.</p>
          </details>
        </li>
      </ol>
      <p v-else>Veřejná rekonstrukce neobsahuje žádné zaznamenané zásahy.</p>
    </details>

    <section class="ending-exports" aria-labelledby="sealed-save-title">
      <h3 id="sealed-save-title">Nech si něco na památku</h3>
      <p>Vlastní vzpomínka uchová tvé zkušenosti a získané stopy. Soukromé rozhovory se do žádného exportu nevkládají.</p>
      <div class="ending-buttons">
        <AwButton v-if="view.ownTeam" variant="primary" @click="$emit('export', true)">Uložit vlastní vzpomínku</AwButton>
        <AwButton @click="$emit('export', false)">Stáhnout veřejnou rekonstrukci</AwButton>
      </div>
    </section>
    <section v-if="view.canManage" class="ending-next" aria-labelledby="sealed-next-title">
      <h3 id="sealed-next-title">Další příběh</h3>
      <p>Vyber scénář a připrav nový případ pro svůj večer. Tento výsledek zůstane uložený.</p>
      <AwButton variant="primary" @click="$emit('new-case')">Založit další případ</AwButton>
    </section>
  </section>
</template>

<style scoped>
.sealed-ending { padding-block: 1.5rem 2rem; }
.ending-context, .ending-line, .entry-chapter { color: var(--wns-muted); }
.ending-context { margin: 0 0 .5rem; }
.sealed-ending h2 { margin: 0; font-size: clamp(2rem, 7vw, 3rem); line-height: 1.12; letter-spacing: -.03em; text-wrap: balance; }
.sealed-ending h2 span { color: var(--wns-mint); }
.ending-line { margin-block: 1rem 1.5rem; }
.ending-reveal { display: grid; gap: 1rem; margin: 0; }
.ending-reveal div { display: grid; gap: .25rem; }
.ending-reveal dt { color: var(--wns-muted); font-size: .875rem; }
.ending-reveal dd { margin: 0; font-weight: 700; overflow-wrap: anywhere; }
.ending-memory, .ending-reconstruction, .ending-exports, .ending-next { margin-top: 2rem; padding-top: 1.5rem; border-top: 1px solid var(--wns-line); }
.sealed-ending h3 { margin: 0 0 .75rem; font-size: 1.125rem; }
.sealed-ending p { overflow-wrap: anywhere; }
.sealed-ending summary { min-height: 44px; padding-block: .5rem; cursor: pointer; font-weight: 700; }
.sealed-ending summary:focus-visible, .sealed-ending :deep(button:focus-visible) { outline: 3px solid var(--wns-mint); outline-offset: 4px; }
.ending-reconstruction ol { list-style: none; padding: 0; }
.ending-reconstruction li { padding-block: 1rem; border-bottom: 1px solid var(--wns-line); }
.ending-reconstruction li p { margin-block: .25rem; }
.ending-rolls { margin-top: .5rem; color: var(--wns-muted); }
.ending-buttons { display: flex; flex-wrap: wrap; gap: .75rem; }
.ending-buttons :deep(button) { flex: 1 1 15rem; min-height: 48px; }
@media (min-width: 600px) { .ending-reveal { grid-template-columns: 1fr 1fr; } }
</style>
