<script setup lang="ts">
import { computed } from 'vue';
import AwButton from '../../components/AwButton.vue';
import type { Clue, Play } from './sealedCase';
import { evidenceChains, evidenceFamilies, sharedEvidence } from './sealedDossier';

const props = defineProps<{ play: Play }>();
const emit = defineEmits<{ share: [clue: Clue] }>();
const chains = computed(() => evidenceChains(props.play.clues));
const claims = computed(() => sharedEvidence(props.play));
const name = (id: string) => props.play.players.find(player => player.id === id)?.name ?? 'Neznámá postava';
const label = (clue: Clue) => clue.label ?? props.play.sources.find(source => source.id === clue.id)?.label
  ?? `Stopa ${props.play.clues.findIndex(part => part.id === clue.id) + 1}`;
</script>

<template>
  <section class="sealed-dossier" aria-labelledby="sealed-evidence-title">
    <h2 id="sealed-evidence-title">Důkazní řetězce</h2>
    <p>Jedno jméno ve třech výpovědích ještě není trojí důkaz. Spojuj různé části A, B a C stejného řetězce.</p>
    <p v-if="!chains.length" class="sealed-empty">Zatím nemáš novou doloženou stopu. Získat ji můžeš pátráním nebo dalším děním v případu.</p>
    <article v-for="chain in chains" :key="chain.id" class="sealed-chain" :aria-label="`Řetězec ${chain.id}`">
      <header class="sealed-chain-heading">
        <h3 :id="`sealed-chain-${chain.id}`">Řetězec {{ chain.id }}</h3>
        <span>{{ 3 - chain.missing.length }} ze 3 částí</span>
      </header>
      <ol class="sealed-chain-links" aria-label="Části řetězce">
        <li v-for="family in evidenceFamilies" :key="family" :class="{ 'is-owned': !chain.missing.includes(family) }">
          <strong>{{ family }}</strong><span>{{ chain.missing.includes(family) ? 'Chybí' : 'Doloženo' }}</span>
        </li>
      </ol>
      <p v-if="chain.missing.length" class="sealed-chain-reading">Chybí {{ chain.missing.join(' a ') }}. Z neúplného řetězce zatím nedělej závěr o viníkovi.</p>
      <p v-else class="sealed-chain-reading"><strong>A, B i C jsou ve tvém spisu.</strong><br />Společný okruh kandidátů: {{ chain.candidates.map(name).join(', ') || 'žádný — porovnej jednotlivé části' }}.</p>
      <details v-for="clue in chain.parts" :key="clue.id" class="sealed-evidence-part">
        <summary :aria-describedby="`sealed-chain-${chain.id}`"><span><span class="sealed-part-family">Část {{ clue.family }}</span><strong>{{ label(clue) }}</strong></span></summary>
        <p v-if="clue.description">{{ clue.description }}</p>
        <p>Okruh kandidátů: {{ clue.candidates.map(name).join(', ') || 'Žádný známý kandidát' }}.</p>
        <small>{{ clue.id }} · {{ clue.sourceEvent.endsWith(':prelude') ? 'Úvod případu' : 'Získáno během hry' }}</small>
        <AwButton v-if="play.ownState !== 'Operator' && play.phase !== 'finished'" :aria-describedby="`sealed-chain-${chain.id}`" @click="emit('share', clue)">Připravit sdílení části {{ clue.family }}</AwButton>
      </details>
    </article>
  </section>
  <section v-if="claims.length" class="sealed-claims" aria-labelledby="sealed-claims-title">
    <h3 id="sealed-claims-title">Sdílená tvrzení o stopách</h3>
    <p class="sealed-muted">Sdělení jsou seřazená podle uvedeného řetězce. Každé zůstává tvrzením autora; do doložených částí se nezapočítává.</p>
    <ol class="sealed-entries">
      <li v-for="(claim, index) in claims" :key="claim.key">
        <p class="sealed-claim-author"><strong>{{ name(claim.statement.actor) }}</strong><span>{{ claim.statement.public ? 'Veřejně' : 'Soukromě' }}</span></p>
        <p>Uvádí řetězec {{ claim.chain }}, část {{ claim.family }}.</p>
        <small v-if="claim.ownClue">Stopu {{ claim.clueId }} máš i ve vlastním spisu. Porovnej obsah; shodný odkaz nepotvrzuje pravdivost zprávy.</small>
        <small v-else>Tuto stopu ve vlastním spisu nemáš.</small>
        <details class="sealed-claim-text">
          <summary :aria-label="`Celé tvrzení ${index + 1}: ${name(claim.statement.actor)}, řetězec ${claim.chain}, část ${claim.family}`">Celé tvrzení</summary>
          <p>{{ claim.statement.body }}</p>
        </details>
      </li>
    </ol>
  </section>
</template>
