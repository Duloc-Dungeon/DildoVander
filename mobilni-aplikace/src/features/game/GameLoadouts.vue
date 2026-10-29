<script setup lang="ts">
import { ref } from 'vue';
import AwButton from '../../components/AwButton.vue';
defineProps<{ busy: boolean; locked?: boolean; uncertain?: boolean }>();
const emit = defineEmits<{ confirm: [loadout: string] }>();
const selected = ref('balanced');
const loadouts = [
  { id: 'balanced', title: 'Rovnováha', stock: '2 Údery · 2 Lsti · 2 Štíty' },
  { id: 'pressure', title: 'Nátlak', stock: '3 Údery · 2 Lsti · 1 Štít' },
  { id: 'trickery', title: 'Úskok', stock: '1 Úder · 3 Lsti · 2 Štíty' },
  { id: 'fortress', title: 'Opevnění', stock: '2 Údery · 1 Lest · 3 Štíty' },
];
</script>
<template>
  <div v-if="locked" role="status"><h2>Sestava uzamčena.</h2><p>Čekáme na soupeře. Obě zásoby odhalíme současně.</p></div>
  <form v-else @submit.prevent="emit('confirm', selected)">
    <fieldset class="game-modes"><legend>Jak vstoupíš do příštího bloku?</legend>
      <label v-for="loadout in loadouts" :key="loadout.id"><input v-model="selected" type="radio" name="loadout" :value="loadout.id" :disabled="busy || uncertain" /><span><strong>{{ loadout.title }}</strong><small>{{ loadout.stock }}</small></span></label>
    </fieldset>
    <p v-if="selected !== 'balanced'">Může ti zbýt jediný typ kouzla. Šetři odpověď na soupeřův tah; soupeř tvou důslednost rád využije.</p>
    <p>Šest použití na čtyři výměny. Jiskry se mezi bloky neobnovují.</p>
    <AwButton type="submit" variant="primary" :busy="busy" :disabled="uncertain">Potvrdit sestavu</AwButton>
  </form>
</template>
