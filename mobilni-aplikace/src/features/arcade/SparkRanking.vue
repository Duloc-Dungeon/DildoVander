<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { readSparkStandings, type SparkStandings } from './sparkApi';
import { gatesLabel } from './sparkCopy';
const standings = ref<SparkStandings>();
const loading = ref(false);
const failed = ref(false);
const search = ref('');
const lifetime = new AbortController();
const normalize = (value: string) => value.normalize('NFD').replace(/\p{Diacritic}/gu, '').toLocaleLowerCase('cs-CZ');
const rows = computed(() => standings.value?.ranking.filter(row => normalize(row.displayName).includes(normalize(search.value.trim()))) ?? []);
const own = computed(() => standings.value?.ranking.find(row => row.id === standings.value?.playerId));
const ahead = computed(() => standings.value?.ranking.filter(row => row.score > (own.value?.score ?? 0)).at(-1));
async function refresh() {
  loading.value = true; failed.value = false;
  try { standings.value = await readSparkStandings(lifetime.signal); }
  catch { if (!lifetime.signal.aborted) failed.value = true; }
  finally { loading.value = false; }
}
onMounted(refresh);
onBeforeUnmount(() => lifetime.abort());
</script>

<template>
  <section class="spark-ranking" aria-label="Kouzlo bez dozoru: pořadí skupiny">
    <p>Nejlepší ověřený pokus. Jedna překonaná brána = jeden bod. Při shodě se místo dělí; další pokusy se nesčítají.</p>
    <RouterLink class="aw-button aw-button--primary" to="/play/kouzlo-bez-dozoru">Hrát Kouzlo bez dozoru</RouterLink>
    <p v-if="loading" role="status">Načítám pořadí…</p>
    <div v-if="failed" role="status"><p>Pořadí se nepodařilo načíst. {{ standings ? 'Zobrazuje se poslední načtený stav.' : 'Zkus spojení znovu.' }}</p><button class="aw-button" @click="refresh">Obnovit pořadí</button></div>
    <template v-if="standings">
      <div v-if="own" class="spark-own"><strong>{{ own.rank }}. místo · {{ gatesLabel(own.score) }}</strong><p v-if="ahead">Další vyšší rekord: {{ ahead.displayName }} · {{ gatesLabel(ahead.score) }}.</p><p v-else>Jsi v čele. Regály už o tobě vědí.</p></div>
      <p v-else>Tvůj první ověřený pokus přidá jméno do žebříčku.</p>
      <label v-if="standings.ranking.length > 8 || search" class="spark-search">Najít hráče<input v-model="search" type="search" placeholder="Jméno postavy" /></label>
      <ol v-if="rows.length" class="game-ranking" aria-label="Pořadí v Kouzlu bez dozoru">
        <li v-for="row in rows" :key="row.id" :value="row.rank" :class="{ 'game-ranking-own': row.id === standings.playerId }" :aria-current="row.id === standings.playerId ? 'true' : undefined">
          <span>{{ row.rank }}.</span><strong>{{ row.displayName }}<small v-if="row.id === standings.playerId"> · ty</small></strong><span>{{ gatesLabel(row.score) }}</span>
        </li>
      </ol>
      <p v-else>{{ search ? 'Tomuto jménu žádný výsledek neodpovídá.' : 'Zatím žádný ověřený pokus. Sklad je podezřele v pořádku.' }}</p>
    </template>
  </section>
</template>

<style scoped>
.spark-ranking { display: grid; gap: 1rem; }
.spark-ranking p { margin: 0; line-height: 1.6; color: var(--aw-muted); }
.spark-own { padding-block: 1rem; border-block: 1px solid var(--aw-border); }
.spark-own strong { color: var(--wns-mint); }
.spark-own p { margin-top: .5rem; }
.spark-search { display: grid; gap: .5rem; }
.spark-search input { min-height: 48px; padding: .75rem; color: var(--aw-ivory); background: var(--aw-elevated); border: 1px solid var(--aw-border); border-radius: 12px; font: inherit; }
.game-ranking li { gap: .75rem; grid-template-columns: auto minmax(0, 1fr) auto; }
.game-ranking li > span:last-child { text-align: right; font-variant-numeric: tabular-nums; }
.game-ranking small { color: var(--wns-mint); }
</style>
