<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import GameCharacter from './GameCharacter.vue';
import GameClash from './GameClash.vue';
import type { Match } from './game';
import './yolo.css';

const props = defineProps<{
  match: Match;
  playerName: string;
  wandUrl?: string;
  wandName?: string;
  synthetic?: boolean;
  opponentWandUrl?: string;
  busy: boolean;
  uncertain: boolean;
}>();
const emit = defineEmits<{ retry: []; cancel: []; revealed: [value: boolean] }>();
const chapter = ref(1);
const showAll = ref(false);
const chapterHeading = ref<HTMLElement>();
const titles = ['Nástup', 'Střet', 'Rozsudek'];
// A partial or stale provider response must never become a finished performance.
const ready = computed(() => props.match.status === 'complete'
  && props.match.yolo?.status === 'complete' && props.match.yolo.beats.length === 3
  && props.match.yolo.beats.every(beat => beat.text.trim()) && Boolean(props.match.yolo.verdict.trim()));
const cancelled = computed(() => ['cancelled', 'declined'].includes(props.match.status));
const failed = computed(() => !cancelled.value && (props.match.yolo?.status === 'failed'
  || props.match.status === 'complete' && !ready.value));
const verdictVisible = computed(() => ready.value && (showAll.value || chapter.value === 3));
const phase = computed(() => cancelled.value ? 'cancelled' : failed.value ? 'failed'
  : !ready.value ? 'preparing' : verdictVisible.value ? 'verdict' : chapter.value === 2 ? 'clash' : 'entrance');
const visibleBeats = computed(() => ready.value ? props.match.yolo!.beats
  .map((beat, index) => ({ ...beat, index }))
  .filter(beat => showAll.value || beat.index === chapter.value - 1) : []);
const statusTitle = computed(() => cancelled.value ? 'Přenos je zrušený.' : failed.value
  ? 'Příběh se nepodařilo dokončit.' : 'Souboj se právě píše.');
watch(() => [props.match.id, props.match.yolo?.status], ([id, status], previous) => {
  if (!previous || id !== previous[0] || status === 'generating') {
    chapter.value = 1;
    showAll.value = false;
  }
});
watch(verdictVisible, value => emit('revealed', value), { immediate: true, flush: 'sync' });
async function advance(all = false) {
  if (!ready.value) return;
  if (all) showAll.value = true;
  else chapter.value = Math.min(3, chapter.value + 1);
  // The triggering button disappears at the finale. Keep keyboard readers in the story.
  await nextTick();
  chapterHeading.value?.focus({ preventScroll: true });
}
</script>

<template>
  <section class="game-yolo yolo-theatre" :data-phase="phase" :data-result="verdictVisible ? match.result : undefined" aria-label="YOLO souboj ve třech dějstvích">
    <div class="yolo-theatre__cast" aria-label="Soupeři">
      <p><strong>{{ playerName }}</strong><span>{{ wandName || 'Tvoje hůlka' }}<span v-if="synthetic" class="yolo-theatre__sample"> · ukázka</span></span></p>
      <span class="yolo-theatre__versus" aria-hidden="true">proti</span>
      <p><strong>{{ match.opponent.displayName }}</strong><span>{{ match.opponent.wandName || 'Soupeřova hůlka' }}<span v-if="match.opponent.wandSynthetic" class="yolo-theatre__sample"> · ukázka</span></span></p>
    </div>
    <div class="yolo-theatre__stage" aria-hidden="true">
      <GameClash theatrical :wand-url="wandUrl" :wand-name="wandName" :synthetic="synthetic"
        :opponent-wand-url="opponentWandUrl" :opponent-wand-name="match.opponent.wandName ?? undefined" :opponent-synthetic="match.opponent.wandSynthetic" />
      <svg v-if="ready" :key="phase" class="yolo-theatre__collision" viewBox="0 0 400 160" preserveAspectRatio="none" focusable="false">
        <path class="yolo-theatre__beam yolo-theatre__beam--player" d="M100 80Q155 30 200 72Q242 112 300 65" />
        <path class="yolo-theatre__beam yolo-theatre__beam--opponent" d="M300 80Q245 30 200 72Q158 112 100 65" />
        <circle class="yolo-theatre__impact" cx="200" cy="72" r="23" />
      </svg>
    </div>
    <ol class="yolo-theatre__acts" aria-label="Dějství příběhu">
      <li v-for="(title, index) in titles" :key="title" :aria-current="ready && !showAll && chapter === index + 1 ? 'step' : undefined" :data-read="ready && (showAll || chapter >= index + 1)">
        <span>{{ index + 1 }}</span>{{ title }}
      </li>
    </ol>
    <div class="yolo-theatre__announcer">
      <GameCharacter character="commentator" :expression="verdictVisible ? 2 : failed ? 1 : 0" />
      <p><strong>Bohumil Křivopísk</strong><span>Rozhodčí bez licence</span></p>
    </div>
    <div v-if="!ready" class="yolo-theatre__status" role="status">
      <h2>{{ statusTitle }}</h2>
      <p>{{ cancelled ? 'Bez vítěze a bez zápisu výhry. Můžeš zpátky ke hře.' : failed ? 'Vítěze nemáme. Zkus přenos znovu, nebo ho zruš bez výsledku.' : 'Křivopísk shání nestranného svědka. Zrcadlo odmítlo.' }}</p>
      <p v-if="!failed && !cancelled" class="yolo-theatre__waiting">Čekáme na celý příběh. Zatím není koho korunovat.</p>
    </div>
    <div v-else class="yolo-theatre__story">
      <h2 ref="chapterHeading" tabindex="-1">{{ showAll ? 'Celý příběh' : titles[chapter - 1] }}</h2>
      <div v-for="beat in visibleBeats" :key="beat.index" class="yolo-theatre__beat">
        <h3 v-if="showAll">{{ beat.index + 1 }}. {{ titles[beat.index] }}</h3>
        <p>{{ beat.text }}</p>
      </div>
      <p v-if="verdictVisible" class="yolo-theatre__verdict">{{ match.yolo!.verdict }}</p>
      <div class="yolo-theatre__reading-controls">
        <AwButton v-if="chapter < 3 && !showAll" variant="primary" @click="advance()">Další část</AwButton>
        <AwButton v-if="!showAll" @click="advance(true)">{{ verdictVisible ? 'Přečíst celý příběh' : 'Ukázat celý výsledek' }}</AwButton>
      </div>
    </div>
    <footer class="yolo-theatre__footer">
      <p>YOLO fikce · příběh i vítěze vybírá AI. Bez bojových XP.</p>
      <div class="yolo-theatre__recovery">
        <AwButton v-if="failed" :busy="busy" :disabled="uncertain" @click="emit('retry')">Opakovat přenos</AwButton>
        <AwButton v-if="!ready && !cancelled" :busy="busy" :disabled="uncertain" @click="emit('cancel')">Zrušit přenos bez výsledku</AwButton>
        <RouterLink v-if="verdictVisible && match.partyTask?.status !== 'pending'" class="aw-button aw-button--primary" :to="{ path: '/play/challenge', query: { mode: 'yolo', opponent: match.opponent.id } }">Odveta</RouterLink>
        <RouterLink v-if="cancelled" to="/play">Zpět ke hře</RouterLink>
      </div>
    </footer>
  </section>
</template>
