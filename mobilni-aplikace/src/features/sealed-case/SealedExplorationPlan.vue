<script setup lang="ts">
import { computed } from 'vue';
import AwButton from '../../components/AwButton.vue';
import type { Play } from './sealedCase';
import { shortestRoute } from './sealedDossier';

const props = defineProps<{ play: Play; actionable?: boolean }>();
const emit = defineEmits<{ travel: [destination: string] }>();
const entries = computed(() => (props.play.sourceSchedule ?? []).map(source => {
  const route = shortestRoute(props.play.routes ?? undefined, props.play.location, source.location);
  const moves = route === null ? null : route.length - 1;
  const remaining = Math.max(0, props.play.chapters - props.play.chapter + (props.play.phase === 'planning' ? 1 : 0));
  const tooLate = moves !== null && Math.max(1, moves) > remaining;
  return { ...source, route, moves, tooLate };
}).sort((a, b) => a.releaseChapter - b.releaseChapter || a.family.localeCompare(b.family)));
const place = (id: string) => props.play.locations[id] ?? id;
const turns = (count: number) => count === 1 ? '1 tah' : count < 5 ? `${count} tahy` : `${count} tahů`;
</script>

<template>
  <details v-if="entries.length" class="sealed-exploration sealed-disclosure" :open="play.chapter === 1 && !play.ownPlan">
    <summary>Plán průzkumu <span class="sealed-summary-count">Zdroje: {{ entries.length }}</span></summary>
    <p>Rozdělte si místa včas. Během jednoho tahu se přesuneš do sousední místnosti a pak jednáš.</p>
    <p class="sealed-muted">Veřejný rozpis ukazuje, kdy se zdroje uvolňují. Dění ve hře může ovlivnit doručení; rozpis neprozrazuje obsah stop.</p>
    <ol class="sealed-source-plan">
      <li v-for="entry in entries" :key="`${entry.family}:${entry.location}:${entry.releaseChapter}`">
        <div class="sealed-source-heading"><strong>{{ entry.label }}</strong><span>Část {{ entry.family }} · od kapitoly {{ entry.releaseChapter }}</span></div>
        <p><strong>{{ place(entry.location) }}</strong></p>
        <p v-if="play.chapters === 3 && entry.family === 'C' && entry.releaseChapter === 3 && play.chapter < 3" class="sealed-muted">Chceš-li zde pátrat po uvolnění stopy C, doraz před začátkem kapitoly 3. Cíl přesunu neslibuje předběžný výběr zdroje.</p>
        <p v-else-if="play.chapters === 3 && entry.family === 'C' && entry.releaseChapter === 3 && play.chapter === 3 && play.location === entry.location" class="sealed-muted">Dorazil jsi včas; zdroj C je teď uvolněný a viditelný.</p>
        <template v-if="entry.route">
          <ol class="sealed-route" aria-label="Nejkratší cesta z tvého místa"><li v-for="(stop, index) in entry.route" :key="index">{{ place(stop) }}</li></ol>
          <p v-if="entry.tooLate" class="sealed-route-warning">Z tvého místa už cestu s pátráním do konce nestihneš. Domluv se s někým blíž.</p>
          <p v-else-if="entry.moves === 0" class="sealed-muted">Jsi na místě. Pátrání stojí jeden tah.</p>
          <p v-else class="sealed-muted">Cesta s pátráním zabere nejméně {{ turns(entry.moves!) }}.</p>
          <AwButton v-if="actionable && play.ownState === 'Awake' && !entry.tooLate && entry.route.length > 1 && play.adjacentLocations.includes(entry.route[1]!)"
            @click="emit('travel', entry.route![1]!)">Připravit přesun: {{ place(entry.route[1]!) }}</AwButton>
        </template>
        <p v-else class="sealed-muted">Cestu z tvého místa teď nelze určit.</p>
      </li>
    </ol>
  </details>
</template>
