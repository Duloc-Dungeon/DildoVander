<script setup lang="ts">
defineProps<{ team?: boolean; practice?: boolean; tactical?: boolean }>();
</script>

<template>
  <details class="game-rules">
    <summary :aria-label="team ? 'Pravidla týmového duelu' : 'Pravidla duelu'">
      <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><circle cx="12" cy="12" r="9" /><path d="M9.5 9a2.5 2.5 0 0 1 5 .5c0 1.5-2.5 2-2.5 3.5M12 16v.1" /></svg>
    </summary>
    <div>
      <strong>Tři kouzla. Jedna volba.</strong>
      <ul class="game-rule-list"><li>Úder přeruší lest.</li><li>Lest obejde štít.</li><li>Štít zastaví úder.</li></ul>
      <p>Vyber kouzlo a potvrď tah. Volby zůstanou tajné až do společného odhalení.</p>
      <p v-if="team">Každý potvrzuje vlastní tah. Po čtyřech výměnách vystřídáte soupeře; celý zápas má osm výměn.</p>
      <p v-if="!practice">Jiskru přisadíš před potvrzením. Když prohraješ, její bod získá {{ team ? 'tým soupeřů' : 'soupeř' }}. Při shodě se přisazené jiskry spotřebují bez bodu.</p>
      <p v-if="tactical">Blok znamená čtyři výměny se společnou zásobou kouzel. Před dalším blokem se zásoba obnoví; při hře s vlastními sestavami ji zvolíš.</p>
    </div>
  </details>
</template>
