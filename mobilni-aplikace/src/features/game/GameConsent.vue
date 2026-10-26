<script setup lang="ts">
const tasks = defineModel<boolean>('tasksEnabled', { required: true });
const adult = defineModel<boolean>('adultConsent', { required: true });
const flirt = defineModel<boolean>('flirtConsent', { required: true });
</script>
<template>
  <fieldset class="game-consent">
    <legend>Dobrovolné proherní úkoly</legend>
    <label><input v-model="tasks" type="checkbox" /> Chci po prohře krátký úkol</label>
    <label v-if="tasks"><input v-model="adult" type="checkbox" /> Je mi 18+ a chci úkoly s dvojsmysly</label>
    <label v-if="tasks && adult"><input v-model="flirt" type="checkbox" /> Chci také kategorii Flirt</label>
    <small>Oba rozhodujete samostatně. Platí společná sada. Úkol můžeš vždy vyměnit nebo přeskočit.</small>
  </fieldset>
</template>
