<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { wnsSound } from '../../application/wnsSound';
import AwButton from '../../components/AwButton.vue';
import type { YoloScenario } from './game';

const props = defineProps<{
  matchId: string;
  scenario: YoloScenario;
  playerName: string;
  opponentName: string;
  choosing: boolean;
  busy: boolean;
  uncertain: boolean;
}>();
const emit = defineEmits<{ choose: [choice: string] }>();
const selected = ref('');
const submitted = ref(false);
const blocked = computed(() => props.busy || props.uncertain || submitted.value || props.scenario.ownChoiceLocked);
watch(() => [props.matchId, props.scenario.id, props.scenario.act], () => {
  selected.value = '';
  submitted.value = false;
});
// A failed request may be retried after the parent has verified its server state.
// Keep the chosen card selected so recovery does not silently change the plan.
watch(() => props.busy, (busy, previous) => {
  if (previous && !busy) submitted.value = false;
});
function confirm() {
  if (!props.choosing || blocked.value || !props.scenario.options.some(option => option.id === selected.value)) return;
  submitted.value = true;
  emit('choose', selected.value);
}
</script>

<template>
  <section class="yolo-decision" aria-label="Rozhodnutí v příběhu">
    <h2>{{ scenario.title }}</h2>
    <p class="yolo-decision__objective"><strong>Cíl: </strong>{{ scenario.objective }}</p>
    <p>{{ scenario.situation }}</p>
    <details v-if="scenario.selectionReason" class="yolo-decision__origin"><summary>Proč právě tato patálie?</summary><p>{{ scenario.selectionReason }}</p></details>
    <template v-if="choosing">
      <p class="yolo-decision__act">Dějství {{ scenario.act }} ze 3 · Oba volíte tajně.</p>
      <p v-if="scenario.ownChoiceLocked" class="yolo-decision__waiting" role="status">Tvůj plán je uzamčený. Čekáme na soupeře; obě volby se odhalí společně.</p>
      <form v-else @submit.prevent="confirm">
        <fieldset class="yolo-decision__options" :disabled="blocked"><legend>Jak budeš postupovat?</legend>
          <label v-for="option in scenario.options" :key="option.id" :class="{ 'is-selected': selected === option.id }">
            <input v-model="selected" type="radio" :name="`yolo-choice-${matchId}`" :value="option.id" @change="wnsSound.local('select')" />
            <span><strong>{{ option.title }}</strong><span class="yolo-decision__benefit"><b>Výhoda: </b>{{ option.benefit }}</span><span class="yolo-decision__cost"><b>Cena: </b>{{ option.cost }}</span></span>
          </label>
        </fieldset>
        <p v-if="scenario.opponentChoiceLocked" class="yolo-decision__waiting">Soupeř má vybráno. Jeho plán zůstává tajný.</p>
        <AwButton type="submit" variant="primary" :busy="busy" :disabled="!selected || blocked">Potvrdit plán</AwButton>
      </form>
    </template>
    <section v-if="scenario.revealed.length" class="yolo-decision__record" aria-label="Odhalené plány a důsledky">
      <h3>Co vaše plány způsobily</h3>
      <ol><li v-for="reveal in scenario.revealed" :key="reveal.act">
        <h4>Dějství {{ reveal.act }}</h4>
        <div class="yolo-decision__consequences">
          <article><h5>{{ playerName }} · {{ reveal.player.title }}</h5><p>{{ reveal.player.consequence }}</p><details><summary>Výhoda a cena</summary><p>{{ reveal.player.benefit }}</p><p>{{ reveal.player.cost }}</p></details></article>
          <article><h5>{{ opponentName }} · {{ reveal.opponent.title }}</h5><p>{{ reveal.opponent.consequence }}</p><details><summary>Výhoda a cena</summary><p>{{ reveal.opponent.benefit }}</p><p>{{ reveal.opponent.cost }}</p></details></article>
        </div>
      </li></ol>
    </section>
  </section>
</template>
