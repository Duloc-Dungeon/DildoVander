<script setup lang="ts">
import { useVisitHumor } from "../humor/visitHumor";
const { copy: humor } = useVisitHumor();
import { computed, ref, watch } from 'vue';
import type { Ranking } from './game';
import SparkRanking from '../arcade/SparkRanking.vue';

const props = defineProps<{ combat: Ranking[]; yolo: Ranking[]; playerId?: string; initialDiscipline?: 'combat' | 'spark' }>();
const discipline = ref<'combat' | 'yolo' | 'spark'>(props.initialDiscipline ?? 'combat');
watch(() => props.initialDiscipline, value => { if (value) discipline.value = value; });
const search = ref('');
const rows = computed(() => discipline.value === 'combat' ? props.combat : props.yolo);
const own = computed(() => rows.value.find(row => row.id === props.playerId));
const score = (row: Ranking) => discipline.value === 'combat' ? row.xp : row.wins;
const number = new Intl.NumberFormat('cs-CZ');
const pointsLabel = (value: number) => `${number.format(value)} ${discipline.value === 'combat' ? 'XP' : value === 1 ? 'výhra' : value >= 2 && value <= 4 ? 'výhry' : 'výher'}`;
const points = (row: Ranking) => pointsLabel(score(row));
const normalize = (value: string) => value.normalize('NFD').replace(/\p{Diacritic}/gu, '').toLocaleLowerCase('cs-CZ');
const filtered = computed(() => rows.value.filter(row => normalize(row.displayName).includes(normalize(search.value.trim()))));
const ahead = computed(() => own.value ? rows.value.filter(row => score(row) > score(own.value!)).at(-1) : undefined);
const shared = computed(() => own.value && rows.value.some(row => row.id !== own.value!.id && row.rank === own.value!.rank));
</script>

<template>
  <section class="game-standings" aria-label="Žebříček hráčů">
    <fieldset class="game-ranking-disciplines">
      <legend class="game-sr">Disciplína</legend>
      <label :class="{ selected: discipline === 'combat' }"><input v-model="discipline" type="radio" name="ranking-discipline" value="combat" /> Bojová XP</label>
      <label :class="{ selected: discipline === 'yolo' }"><input v-model="discipline" type="radio" name="ranking-discipline" value="yolo" /> YOLO výhry</label>
      <label :class="{ selected: discipline === 'spark' }"><input v-model="discipline" type="radio" name="ranking-discipline" value="spark" /> Kouzlo bez dozoru</label>
    </fieldset>
    <SparkRanking v-if="discipline === 'spark'" />
    <template v-else>
    <p class="game-ranking-explanation">{{ discipline === 'combat' ? 'Pořadí podle získaných bojových XP. Při shodě se místo dělí.' : 'Pořadí podle vítězství v YOLO lóži. Tyto výhry nepřidávají bojovou sílu.' }}</p>
    <div v-if="own" class="game-ranking-position" role="status">
      <p><strong>{{ shared ? 'Dělíš se o' : 'Držíš' }} {{ own.rank }}. místo</strong><span>{{ points(own) }}</span></p>
      <p v-if="ahead">Odstup od další vyšší příčky: {{ pointsLabel(score(ahead) - score(own)) }}.</p>
      <p v-else-if="score(own) > 0">{{ shared ? humor.sharedLead : humor.soloLead }}</p>
      <p v-else>První výsledek teprve rozdá místa.</p>
    </div>
    <p v-else-if="rows.length" role="status">Tvoje jméno se v pořadí objeví, jakmile tě skupina zařadí mezi hráče.</p>
    <label v-if="rows.length > 8 || search" class="game-ranking-search">Najít hráče<input v-model="search" type="search" placeholder="Jméno postavy" /></label>
    <template v-if="rows.length">
      <p class="game-ranking-count">{{ search ? `${filtered.length} z ${rows.length}` : rows.length }} hráčů · {{ discipline === 'combat' ? 'bojová XP' : 'YOLO výhry' }}</p>
      <ol v-if="filtered.length" class="game-ranking" aria-label="Pořadí hráčů">
        <li v-for="row in filtered" :key="row.id" :value="row.rank" :class="{ 'game-ranking-own': row.id === playerId, 'game-ranking-leader': row.rank === 1 }" :aria-current="row.id === playerId ? 'true' : undefined">
          <span class="game-ranking-place">{{ row.rank }}.</span>
          <span class="game-ranking-name"><strong>{{ row.displayName }}</strong><small v-if="row.id === playerId">Ty</small></span>
          <span class="game-ranking-points">{{ points(row) }}</span>
        </li>
      </ol>
      <p v-else role="status">Tomuto jménu žádný hráč neodpovídá. Zkus kratší jméno.</p>
    </template>
    <p v-else role="status">V této disciplíně zatím nejsou žádní hráči. {{ humor.emptyRanking }}</p>
    </template>
  </section>
</template>

<style scoped>
.game-standings { display: grid; gap: 1.25rem; }
.game-ranking-disciplines { display: flex; flex-wrap: wrap; gap: .5rem; border: 0; padding: 0; margin: 0; }
.game-ranking-disciplines label { display: flex; flex: 1 1 9rem; align-items: center; justify-content: center; gap: .5rem; min-height: 48px; padding: .65rem .8rem; border: 1px solid var(--aw-border); border-radius: 14px; cursor: pointer; }
.game-ranking-disciplines label.selected { background: var(--wns-mint); color: var(--wns-on-mint, #102419); border-color: var(--wns-mint); font-weight: 760; }
.game-ranking-disciplines label:focus-within { outline: 2px solid var(--wns-mint); outline-offset: 4px; }
.game-ranking-disciplines input { accent-color: var(--wns-on-mint, #102419); }
.game-ranking-explanation, .game-ranking-count { color: var(--aw-muted); margin: 0; }
.game-ranking-position { padding-block: 1rem; border-block: 1px solid var(--aw-border); }
.game-ranking-position p { margin: 0; }
.game-ranking-position p:first-child { display: flex; flex-wrap: wrap; justify-content: space-between; gap: .5rem; }
.game-ranking-position strong { color: var(--wns-mint); }
.game-ranking-position p + p { margin-top: .35rem; color: var(--aw-muted); }
.game-ranking-search { display: grid; gap: .5rem; }
.game-ranking-search input { width: 100%; min-width: 0; min-height: 48px; border: 1px solid var(--aw-border); border-radius: 14px; background: var(--aw-elevated); color: var(--aw-ivory); padding: .75rem; font: inherit; }
.game-ranking-search input::placeholder { color: var(--aw-muted); }
.game-ranking { margin: -.5rem 0 0; }
.game-ranking li { padding: .9rem .5rem; grid-template-columns: minmax(2ch, auto) minmax(0, 1fr) auto; gap: .75rem; }
.game-ranking li.game-ranking-own { border-radius: 8px; }
.game-ranking-place, .game-ranking-points, .game-ranking-position span { font-variant-numeric: tabular-nums; }
.game-ranking-leader .game-ranking-place { color: var(--wns-amber); font-weight: 800; }
.game-ranking-name { display: flex; flex-wrap: wrap; align-items: baseline; gap: .25rem .5rem; min-width: 0; }
.game-ranking-name small { color: var(--wns-mint); }
.game-ranking-points { text-align: right; }
@media (max-width: 360px) { .game-ranking li { gap: .5rem; padding-inline: .25rem; } .game-ranking-points { max-width: 6ch; overflow-wrap: normal; } }
</style>
