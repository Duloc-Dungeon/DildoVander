<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import SceneShell from '../../components/scene/SceneShell.vue';
import PaperSurface from '../../components/scene/PaperSurface.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import tableScene from '../../assets/brand/responsive-table/table-scene.webp';
import strikeToken from '../../assets/brand/responsive-table/token-strike.webp';
import trickToken from '../../assets/brand/responsive-table/token-trick.webp';
import shieldToken from '../../assets/brand/responsive-table/token-shield.webp';
import GameCharacter from './GameCharacter.vue';
import GameConsent from './GameConsent.vue';
import GameLoadouts from './GameLoadouts.vue';
import { account } from '../identity/session';
import { actions, actionName, modeName, gameRequest, gameErrorText, GameError, operation, practiceRound,
  type Choice, type GameMode, type Lobby, type Match, type Round, type BossProfile, type Progress, type Milestone } from './game';
import './game.css';

const route = useRoute();
const router = useRouter();
const lobby = ref<Lobby>();
const bossProfile = ref<BossProfile>();
const progress = ref<Progress>();
const milestoneDismissed = ref(false);
const rankingDiscipline = ref('combat');
const showEntireRanking = ref(false);
const match = ref<Match>();
const busy = ref(false);
const loading = ref(true);
const error = ref('');
const notice = ref('');
const uncertain = ref(false);
const opponentId = ref(typeof route.query.opponent === 'string' ? route.query.opponent : '');
const mode = ref<GameMode>(route.query.mode === 'yolo' ? 'yolo' : route.query.mode === 'tactical' ? 'tactical' : 'quick');
const useLoadouts = ref(false);
const tasksEnabled = ref(false);
const adultConsent = ref(false);
const flirtConsent = ref(false);
const selected = ref<Choice>();
const spark = ref(false);
const showAllStory = ref(false);
const storyStep = ref(1);
const practice = computed(() => route.path === '/play/practice');
const chronicle = computed(() => route.path === '/play/chronicle');
const challenge = computed(() => route.path === '/play/challenge');
const bossSetup = computed(() => route.path === '/play/boss');
const bossAttempt = computed(() => route.path.startsWith('/play/boss/'));
const keeper = computed(() => route.path === '/play/keeper');
const ranking = computed(() => route.path === '/play/ranking');
const messages = computed(() => route.path === '/play/messages');
const rankings = computed(() => rankingDiscipline.value === 'combat' ? progress.value?.combatRanking ?? [] : progress.value?.yoloRanking ?? []);
const visibleRankings = computed(() => {
  if (showEntireRanking.value) return rankings.value;
  const index = rankings.value.findIndex(row => row.id === account.value?.id);
  return rankings.value.slice(Math.max(0, index - 1), Math.max(3, index + 2));
});
const matchId = computed(() => typeof route.params.id === 'string' ? route.params.id : '');
const practiceRounds = ref<Round[]>([]);
const practiceDone = computed(() => practiceRounds.value.length === 7);
const playerScore = computed(() => practice.value ? practiceRounds.value.filter(round => round.winner === 'player').length : match.value?.playerScore ?? 0);
const opponentScore = computed(() => practice.value ? practiceRounds.value.filter(round => round.winner === 'opponent').length : match.value?.opponentScore ?? 0);
const completed = computed(() => practice.value ? practiceDone.value : match.value?.status === 'complete');
const tableDuel = computed(() => practice.value || Boolean(match.value && match.value.mode !== 'yolo'
  && (match.value.status === 'active' || match.value.status === 'complete')));
const roundCount = computed(() => practice.value ? 7 : match.value?.totalRounds ?? 0);
const completedRoundCount = computed(() => practice.value ? practiceRounds.value.length : match.value?.completedRounds ?? 0);
const playerName = computed(() => account.value?.displayName || 'Ty');
const opponentName = computed(() => practice.value ? 'Cvičný mág' : match.value?.opponent.displayName || 'Soupeř');
const spellTokens = { strike: strikeToken, trick: trickToken, shield: shieldToken };
const selectedAction = computed(() => actions.find(action => action.id === selected.value));
const lastRound = computed(() => practice.value ? practiceRounds.value.at(-1) : match.value?.rounds.at(-1));
const selectedOpponent = computed(() => lobby.value?.opponents.find(opponent => opponent.id === opponentId.value));
const advanced = computed(() => mode.value === 'tactical' && lobby.value?.player.tacticalUnlocked && (selectedOpponent.value?.xp ?? 0) >= 30);
const canStart = computed(() => Boolean(opponentId.value) && (mode.value === 'quick' || mode.value === 'tactical' && advanced.value || mode.value === 'yolo' && lobby.value?.player.yoloUnlocked && (selectedOpponent.value?.matchesPlayed ?? 0) >= 3));
const nextMilestone = computed(() => {
  const player = lobby.value?.player;
  if (!player) return '';
  if (player.matchesPlayed < 2) return 'Dva dokončené duely odhalí jiskry.';
  if (player.xp < 30) return `${player.xp} / 30 XP · Hůlka II je blízko`;
  return 'Hůlka II · Teď plánuješ zásobu kouzel';
});
const resultTitle = computed(() => {
  if (match.value?.mode === 'boss') return match.value.result === 'win' ? 'Baron padl. Opona se zvedá.' : 'Tentokrát vyhrál Baron.';
  const result = match.value?.result ?? (playerScore.value === opponentScore.value ? 'draw' : playerScore.value > opponentScore.value ? 'win' : 'loss');
  if (result === 'draw') return 'Remíza. Magie váhá.';
  return result === 'win' ? 'Tohle bylo tvoje.' : 'Dnes slaví soupeř.';
});
const liveMessage = computed(() => {
  if (busy.value) return 'Potvrzujeme tah…';
  if (uncertain.value) return 'Výsledek odeslání ověřujeme se serverem.';
  if (match.value?.ownChoiceLocked) return 'Tvůj tah je uzamčený. Čekáme na soupeře.';
  if (completed.value) return resultTitle.value;
  return '';
});
const consent = () => ({ tasksEnabled: tasksEnabled.value, adultConsent: tasksEnabled.value && adultConsent.value, flirtConsent: tasksEnabled.value && adultConsent.value && flirtConsent.value, useLoadouts: useLoadouts.value });
let controller = new AbortController();
let generation = 0;
let refreshing = false;
let timer: ReturnType<typeof setInterval> | undefined;

function updateMatch(value: Match) {
  if (matchId.value && value.id !== matchId.value) return;
  if (match.value?.id === value.id && value.revision < match.value.revision) return;
  if (match.value?.id !== value.id || match.value?.completedRounds !== value.completedRounds) {
    selected.value = undefined; spark.value = false;
  }
  match.value = value;
}
async function refresh() {
  if (practice.value) { loading.value = false; return; }
  if (refreshing || busy.value) return;
  refreshing = true;
  const token = generation;
  try {
    if (matchId.value) {
      const value = await gameRequest<Match>(`${bossAttempt.value ? '/boss/attempts' : '/matches'}/${encodeURIComponent(matchId.value)}`, undefined, controller.signal);
      if (token === generation) updateMatch(value);
    } else {
      const value = await gameRequest<Lobby>('', undefined, controller.signal);
      const nextProgress = await gameRequest<Progress>('/progress', undefined, controller.signal).catch(() => undefined);
      const nextBoss = bossSetup.value ? await gameRequest<BossProfile>('/boss', undefined, controller.signal) : undefined;
      if (token === generation) {
        lobby.value = value;
        if (nextProgress?.pending) progress.value = nextProgress;
        if (nextBoss) bossProfile.value = nextBoss;
        if (!opponentId.value || !value.opponents.some(opponent => opponent.id === opponentId.value)) opponentId.value = value.opponents[0]?.id ?? '';
        const resume = value.active.find(item => item.status === 'active');
        if (loading.value && route.path === '/play' && route.query.browse !== '1' && resume) void router.replace(`/play/match/${resume.id}`);
        else if (loading.value && route.path === '/play' && route.query.browse !== '1' && value.boss?.activeAttemptId) void router.replace(`/play/boss/${value.boss.activeAttemptId}`);
      }
    }
    if (token === generation) { error.value = ''; uncertain.value = false; }
  } catch (cause) {
    if (token === generation && !controller.signal.aborted) {
      error.value = gameErrorText(cause);
      if (cause instanceof GameError && [401, 403, 404].includes(cause.status)) {
        match.value = undefined; lobby.value = undefined; progress.value = undefined; bossProfile.value = undefined;
      }
    }
  } finally {
    if (token === generation) { refreshing = false; loading.value = false; }
  }
}
async function acknowledge(milestone: Milestone, open: boolean) {
  if (busy.value || !progress.value) return;
  busy.value = true; const token = generation;
  try {
    await gameRequest('/progress/ack', { operationId: operation(), expectedRevision: progress.value.revision, eventId: milestone.id }, controller.signal);
    if (token === generation) {
      milestoneDismissed.value = true;
      if (open) await router.push(milestone.destination);
    }
  } catch (cause) { if (token === generation) error.value = gameErrorText(cause); }
  finally { if (token === generation) busy.value = false; }
}
async function mutate(action: string, fields: Record<string, unknown> = {}) {
  if (busy.value || !match.value || uncertain.value) return;
  busy.value = true; error.value = ''; notice.value = '';
  const token = generation;
  const current = match.value;
  try {
    const value = await gameRequest<Match>(`${current.mode === 'boss' ? '/boss/attempts' : '/matches'}/${current.id}/${action}`, {
      operationId: operation(), expectedRevision: current.revision, ...fields,
    }, controller.signal);
    if (token === generation) updateMatch(value);
  } catch (cause) {
    if (token === generation && !controller.signal.aborted) {
      error.value = gameErrorText(cause);
      uncertain.value = true;
      if (cause instanceof GameError && [401, 403, 404].includes(cause.status)) {
        match.value = undefined; uncertain.value = false;
      }
    }
  } finally {
    if (token === generation) { busy.value = false; if (uncertain.value) void refresh(); }
  }
}
async function startBoss() {
  if (busy.value || !bossProfile.value?.revealed) return;
  busy.value = true; error.value = '';
  const token = generation;
  try {
    const value = await gameRequest<Match>('/boss/attempts', { operationId: operation(), expectedRevision: bossProfile.value.revision }, controller.signal);
    if (token === generation) await router.push(`/play/boss/${value.id}`);
  } catch (cause) {
    if (token === generation && !controller.signal.aborted) { error.value = gameErrorText(cause); uncertain.value = true; }
  } finally { if (token === generation) { busy.value = false; if (uncertain.value) void refresh(); } }
}
async function start() {
  if (busy.value || !canStart.value) return;
  busy.value = true; error.value = '';
  const token = generation;
  try {
    const value = await gameRequest<Match>('/matches', { opponentId: opponentId.value, mode: mode.value, operationId: operation(), ...consent() }, controller.signal);
    if (token === generation) await router.push(`/play/match/${value.id}`);
  } catch (cause) {
    if (token === generation && !controller.signal.aborted) {
      error.value = `${gameErrorText(cause)} Před novou výzvou zkontroluj rozehrané zápasy.`;
      uncertain.value = true;
    }
  } finally { if (token === generation) busy.value = false; }
}
function submitChoice() {
  if (!selected.value) return;
  if (!practice.value) { void mutate('choose', { choice: selected.value, sparkBid: spark.value ? 1 : 0 }); return; }
  if (practiceDone.value) return;
  const draw = new Uint32Array(1); crypto.getRandomValues(draw);
  const result = practiceRound(selected.value, actions[draw[0] % 3].id);
  practiceRounds.value.push({ ...result, index: practiceRounds.value.length + 1 });
  selected.value = undefined;
}
function resetPractice() { practiceRounds.value = []; selected.value = undefined; }
function roundText(round: Round) {
  if (round.playerChoice === round.opponentChoice) return `${actionName(round.playerChoice)} proti ${actionName(round.opponentChoice)}. Remíza, bez bodů.`;
  const won = round.winner === 'player';
  const points = won ? round.playerPoints : round.opponentPoints;
  return `${actionName(round.playerChoice)} proti ${actionName(round.opponentChoice)}. ${won ? 'Výměnu získáváš ty.' : 'Výměnu získává soupeř.'}${points ? ` +${points} ${points === 1 ? 'bod' : points < 5 ? 'body' : 'bodů'}.` : ''}`;
}
function available(choice: Choice) {
  return practice.value || match.value?.exhibition || match.value?.mode !== 'tactical' && match.value?.mode !== 'boss' || (match.value?.playerInventory?.[choice] ?? 2) > 0;
}
function onVisible() { if (!document.hidden) void refresh(); }
watch(() => account.value?.id, (value, previous) => {
  if (value === previous) return;
  generation++; controller.abort(); controller = new AbortController();
  match.value = undefined; lobby.value = undefined; bossProfile.value = undefined; progress.value = undefined; refreshing = false;
  busy.value = false; uncertain.value = false; selected.value = undefined;
  tasksEnabled.value = false; adultConsent.value = false; flirtConsent.value = false;
  void refresh();
});
onMounted(() => {
  void refresh(); timer = setInterval(onVisible, 3000);
  document.addEventListener('visibilitychange', onVisible); window.addEventListener('online', onVisible);
});
onBeforeUnmount(() => {
  generation++; controller.abort(); if (timer) clearInterval(timer);
  document.removeEventListener('visibilitychange', onVisible); window.removeEventListener('online', onVisible);
});
</script>

<template>
  <SceneShell class="game-scene" :class="{ 'responsive-table': tableDuel }" :background="tableDuel ? tableScene : undefined" labelled-by="game-title">
    <div class="game-content">
      <header class="game-header">
        <RouterLink :to="route.path === '/play' ? '/me/character' : { path: '/play', query: { browse: '1' } }">← {{ route.path === '/play' ? 'Moje postava' : 'Ke hře' }}</RouterLink>
        <span class="game-eyebrow">{{ tableDuel ? practice ? 'Trénink · bez XP' : modeName(match!.mode) : 'Wand Night Stand' }}</span>
        <details v-if="tableDuel" class="game-rules"><summary aria-label="Pravidla duelu">?</summary><div><strong>Tři kouzla. Jedna volba.</strong><p>Úder přeruší lest. Lest obejde štít. Štít zastaví úder.</p><p>Vyber kouzlo a potvrď tah. Volby zůstanou tajné až do společného odhalení.</p><p v-if="!practice">Jiskru přisadíš před potvrzením. Když prohraješ, její bod získá soupeř. Při shodě se přisazené jiskry spotřebují bez bodu.</p></div></details>
      </header>
      <div v-if="error" class="game-feedback"><AwNotice tone="danger">{{ error }}</AwNotice><AwButton :busy="loading" @click="refresh">Obnovit stav</AwButton></div>
      <p v-if="loading && !match && !lobby && !practice" role="status">Připravujeme tvé místo u stolu…</p>
      <p class="game-sr" role="status" aria-live="polite">{{ liveMessage }}</p>

      <template v-if="practice || match">
        <div class="game-match-heading" :class="{ 'game-sr': tableDuel }">
          <p class="game-eyebrow">{{ practice ? 'Trénink · bez XP' : modeName(match!.mode) }}</p>
          <h1 id="game-title">{{ practice ? 'Zkus kouzlo.' : match!.opponent.displayName }}</h1>
        </div>
        <PaperSurface v-if="!practice && match?.status === 'incoming'" class="game-paper">
          <p class="game-eyebrow">Výzva u stolu</p>
          <h2>{{ modeName(match.mode) }}</h2>
          <p>{{ match.mode === 'tactical' ? 'Osm výměn, omezená zásoba kouzel. Přibližně 2–3 minuty.' : match.mode === 'yolo' ? 'Vítěze vymýšlí AI. Hrajeme o historku a slávu, bez bojových XP.' : 'Sedm tajných výměn. Přibližně minuta a půl.' }}</p>
          <p v-if="match.mode === 'yolo'">OpenAI použije herní jméno, svět, název hůlky a nejvýše tři herní rysy každé postavy.</p>
          <GameConsent v-if="match.canAccept" v-model:tasks-enabled="tasksEnabled" v-model:adult-consent="adultConsent" v-model:flirt-consent="flirtConsent" />
          <label v-if="match.canAccept && match.loadoutsAvailable" class="game-spark"><input v-model="useLoadouts" type="checkbox" /> Chci v tomto zápase volit vlastní sestavu</label>
          <div class="game-stack">
            <AwButton v-if="match.canAccept" variant="primary" :busy="busy" @click="mutate('accept', consent())">Přijmout výzvu</AwButton>
            <AwButton v-if="match.canAccept" :busy="busy" @click="mutate('decline')">Odmítnout</AwButton>
            <p v-if="match.canCancel" role="status">Výzva odeslána. Čekáme na souhlas soupeře.</p>
            <AwButton v-if="match.canCancel" :busy="busy" @click="mutate('cancel')">Zrušit výzvu</AwButton>
          </div>
        </PaperSurface>

        <PaperSurface v-else-if="match?.needsLoadout" class="game-paper"><GameLoadouts :key="match.block" :busy="busy" :uncertain="uncertain" :locked="match.ownLoadoutLocked" @confirm="loadout => mutate('loadout', { loadout })" /></PaperSurface>
        <template v-else-if="practice || match?.status === 'active' || completed || match?.mode === 'yolo'">
          <div v-if="match?.mode === 'yolo'" class="game-yolo">
            <GameCharacter character="commentator" :expression="completed ? 2 : 0" />
            <PaperSurface class="game-paper">
              <p class="game-eyebrow">Rozhodčí bez licence</p><h2>Bohumil Křivopísk</h2>
              <p v-if="!match.yolo?.beats?.length">{{ match.yolo?.status === 'failed' ? 'Přenos se nepovedl. Zatím nemáme vítěze.' : 'Křivopísk hledá pravidla. Zatím neúspěšně.' }}</p>
              <template v-else><p v-for="(beat, index) in match.yolo.beats.slice(0, showAllStory ? 3 : storyStep)" :key="index">{{ beat.text }}</p>
                <AwButton v-if="storyStep < 3 && !showAllStory" @click="storyStep++">Další část</AwButton>
                <AwButton v-if="!showAllStory" @click="showAllStory = true">Ukázat celý výsledek</AwButton>
                <p v-if="showAllStory || storyStep === 3"><strong>{{ match.yolo.verdict }}</strong></p>
              </template>
              <p>YOLO fikce · bez bojových XP</p>
              <AwButton v-if="match.yolo?.status === 'failed'" :busy="busy" @click="mutate('retry')">Opakovat přenos</AwButton>
              <AwButton v-if="match.yolo?.status === 'generating' || match.yolo?.status === 'failed'" :busy="busy" @click="mutate('cancel')">Zrušit přenos bez výsledku</AwButton>
              <RouterLink v-if="match.yolo?.status === 'complete'" class="aw-button aw-button--primary" :to="{path: '/play/challenge', query: {mode: 'yolo', opponent: match.opponent.id}}">Odveta</RouterLink>
            </PaperSurface>
          </div>
          <template v-else>
            <div v-if="match?.mode === 'boss'" class="game-boss-stage">
              <GameCharacter character="boss" :expression="match.result === 'win' ? 2 : (match.opponentEnergy ?? 5) <= 2 ? 1 : 0" />
              <div><p class="game-eyebrow">Fáze {{ match.phase }} ze 3</p><p>{{ match.hint }}</p><p v-if="match.bossSpark"><strong>Baron v této výměně přisazuje jiskru.</strong></p><p>Síla tvé hůlky: {{ match.strength?.toLocaleString('cs-CZ', { maximumFractionDigits: 2 }) }}×</p></div>
            </div>
            <div v-if="match?.mode === 'boss'" class="game-score" aria-label="Zbývající bojová energie">
              <div><span>Tvoje energie</span><strong>{{ match.playerEnergy?.toLocaleString('cs-CZ', { maximumFractionDigits: 2 }) }} / 6</strong></div><span class="game-score-divider" aria-hidden="true">·</span><div><span>Baronova energie</span><strong>{{ match.opponentEnergy?.toLocaleString('cs-CZ', { maximumFractionDigits: 2 }) }} / 5</strong></div>
            </div>
            <div v-else class="game-score game-score--duel" aria-label="Skóre zápasu">
              <span class="game-player-avatar" aria-hidden="true">{{ playerName.slice(0, 1) }}</span>
              <div><span>{{ playerName }}</span><strong :key="`player-${playerScore}`">{{ playerScore }}</strong></div>
              <span class="game-score-divider" aria-hidden="true">:</span>
              <div><span>{{ opponentName }}</span><strong :key="`opponent-${opponentScore}`">{{ opponentScore }}</strong></div>
              <span class="game-player-avatar game-player-avatar--opponent" aria-hidden="true">{{ opponentName.slice(0, 1) }}</span>
            </div>
            <p class="game-round-label">{{ completed ? 'Dohráno' : `Výměna ${Math.min((practice ? practiceRounds.length : match!.completedRounds) + 1, practice ? 7 : match!.totalRounds)} ze ${practice ? 7 : match!.totalRounds}` }}<span v-if="match?.mode === 'tactical'"> · blok {{ match.block }}</span></p>
            <div class="game-round-dots" aria-hidden="true"><span v-for="round in roundCount" :key="round" :class="{ done: round <= completedRoundCount, current: !completed && round === completedRoundCount + 1 }" /></div>
            <div class="game-table-stage">
            <div class="game-clash-stage" :key="`clash-${completedRoundCount}`" :class="{ 'game-clash-stage--revealed': lastRound, 'game-clash-stage--locked': match?.ownChoiceLocked }" aria-hidden="true"><span v-if="match?.ownChoiceLocked">Tah uzamčený<span>Čekáme na soupeře</span></span></div>
            <PaperSurface v-if="lastRound" class="game-paper game-reveal" :key="completedRoundCount" aria-live="polite" aria-atomic="true">
              <p class="game-eyebrow">{{ lastRound ? 'Poslední odhalení' : 'Tři kouzla. Jedna volba.' }}</p>
              <p v-if="lastRound">{{ roundText(lastRound) }}</p>
              <p v-else>Úder přeruší lest. Lest obejde štít. Štít zastaví úder. Oba hráči volí tajně.</p>
            </PaperSurface>
            </div>
            <PaperSurface v-if="completed" class="game-paper game-result">
              <p class="game-eyebrow">{{ practice ? 'Trénink dokončen' : 'Výsledek' }}</p><h2>{{ resultTitle }}</h2>
              <p v-if="!practice" class="game-xp">+{{ match!.xpAwarded }} XP</p><p v-else>Teď znáš všechny tři akce. Trénink nepřidává XP ani úkoly.</p>
              <RouterLink v-if="!practice" class="aw-button aw-button--primary" :to="match?.mode === 'boss' ? '/play/boss' : { path: '/play/challenge', query: { opponent: match!.opponent.id, mode: match!.mode } }">Odveta</RouterLink>
              <AwButton v-else variant="primary" @click="resetPractice">Ještě jeden trénink</AwButton>
              <RouterLink to="/play">Zpět ke stolu</RouterLink>
            </PaperSurface>
            <PaperSurface v-else class="game-paper game-controls">
              <p v-if="match?.ownChoiceLocked" role="status">Tvůj tah je uzamčený. Čekáme na soupeře.</p>
              <template v-else>
                <fieldset class="game-spells"><legend>Vyber kouzlo</legend>
                  <label v-for="action in actions" :key="action.id" :class="{ selected: selected === action.id, exhausted: !available(action.id) }">
                    <input v-model="selected" type="radio" name="spell" :value="action.id" :disabled="busy || uncertain || !available(action.id)" />
                    <img class="game-spell-token" :src="spellTokens[action.id]" alt="" width="110" height="110" /><strong>{{ action.name }}</strong><small>{{ action.beats }}</small>
                    <span v-if="match?.mode === 'tactical' || match?.mode === 'boss'">{{ match.exhibition ? 'Neomezeně' : available(action.id) ? `${match.playerInventory?.[action.id] ?? 2} použití` : 'Vyčerpáno' }}</span>
                  </label>
                </fieldset>
                <p class="game-choice-explanation" aria-live="polite">{{ selectedAction ? `${selectedAction.name}. ${selectedAction.beats}.` : 'Úder přeruší lest. Lest obejde štít. Štít zastaví úder.' }}</p>
                <label v-if="!practice && (match!.sparksEnabled || match!.playerSparks > 0)" class="game-spark"><input v-model="spark" type="checkbox" :disabled="!match!.playerSparks || busy || uncertain" /><span>{{ match!.playerSparks ? `Přisadit jiskru · zbývají ${match!.playerSparks}` : 'Jiskry vyčerpány' }}<small>Když prohraješ, její bod získá soupeř.</small></span><span class="game-spark-marks" aria-hidden="true"><span v-for="mark in match!.playerSparks" :key="mark" :class="{ 'game-spark-mark--bid': spark && mark === 1 }">✦</span></span></label>
                <AwButton variant="primary" :busy="busy" :disabled="!selected || uncertain" @click="submitChoice">Potvrdit tah</AwButton>
                <p v-if="match?.opponentChoiceLocked" class="game-helper">Soupeř je připraven. Jeho tah zůstává tajný.</p>
              </template>
            </PaperSurface>
          </template>
          <p v-if="match?.opponentInventory && (match.mode === 'boss' || match.mode === 'tactical')" class="game-helper">Soupeři zbývá: Úder {{ match.opponentInventory.strike }}, Lest {{ match.opponentInventory.trick }}, Štít {{ match.opponentInventory.shield }}.</p>
          <AwButton v-if="match?.mode === 'boss' && match.status === 'active'" :busy="busy" @click="mutate('cancel')">Ukončit pokus bez výsledku</AwButton>
          <AwButton v-if="match?.canCancel && match.status === 'active' && match.mode !== 'boss'" :busy="busy" @click="mutate('cancel')">Ukončit zápas bez výsledku</AwButton>
          <PaperSurface v-if="match?.partyTask" class="game-paper game-task">
            <p class="game-eyebrow">Dobrovolný úkol · {{ match.partyTask.category === 'classic' ? 'Klasika' : match.partyTask.category === 'adult' ? 'Dvojsmysly 18+' : 'Flirt 18+' }}</p><h2>{{ match.partyTask.title }}</h2><p>{{ match.partyTask.text }}</p>
            <div v-if="match.result === 'loss' && match.partyTask.status === 'pending'" class="game-stack"><AwButton :busy="busy" @click="mutate('task', { taskAction: 'done' })">Hotovo</AwButton><AwButton :busy="busy" @click="mutate('task', { taskAction: 'swap', taskCategory: match!.partyTask!.category })">Jiný úkol</AwButton><AwButton v-if="match.flirtConsent && match.partyTask.category !== 'flirt'" :busy="busy" @click="mutate('task', { taskAction: 'swap', taskCategory: 'flirt' })">Vybrat Flirt 18+</AwButton><AwButton :busy="busy" @click="mutate('task', { taskAction: 'skip' })">Přeskočit</AwButton></div>
            <p v-else>V pořádku. Pokračujeme.</p>
          </PaperSurface>
          <AwButton v-if="match?.adultConsent" :busy="busy" @click="mutate('task', { taskAction: 'revoke' })">Odvolat souhlas s Adult</AwButton>
          <p v-if="match?.tasksEnabled" class="game-helper">Úkoly: {{ match.adultConsent ? match.flirtConsent ? '18+ dvojsmysly a Flirt' : '18+ dvojsmysly' : 'klasické' }} · vždy dobrovolné</p>
          <details v-if="lastRound" class="game-history"><summary>Průběh zápasu</summary><ol><li v-for="round in practice ? practiceRounds : match!.rounds" :key="round.index">{{ roundText(round) }}</li></ol></details>
        </template>
        <PaperSurface v-else class="game-paper"><h2>Zápas byl {{ match?.status === 'declined' ? 'odmítnut' : 'ukončen' }}.</h2><p>Bez vítěze a bez XP. Můžeš poslat novou výzvu.</p><RouterLink to="/play">Zpět ke stolu</RouterLink></PaperSurface>
      </template>

      <template v-else-if="lobby">
        <template v-if="keeper">
          <template v-if="progress?.keeperState === 'captured'">
            <p class="game-eyebrow">Ollivander je v zajetí.</p><h1 id="game-title">Dílnu převzal Baron.</h1><GameCharacter class="game-boss-portrait" character="boss" />
            <PaperSurface class="game-paper"><p>„Osobní obsluha? Zrušena. Reklamace? Zamítnuta. Další otázky po mém vítězství.“</p><RouterLink class="aw-button aw-button--primary" to="/play/boss">Vyzvat bosse</RouterLink><p v-if="lobby.player.xp >= 30">Tvoje hůlka II zůstává dostupná ze samoobslužné zásuvky. O své XP ani nárok nepřicházíš.</p></PaperSurface>
          </template>
          <template v-else><p class="game-eyebrow">Digitální dílna</p><h1 id="game-title">Ollivander má slovo.</h1><PaperSurface class="game-paper"><h2>{{ progress?.keeperState === 'rescued' ? 'Vždycky jsem ti věřil. Skoro.' : lobby.player.xp >= 30 ? 'Tohle dřevo už ti nestačí.' : 'Každá hůlka začíná od tří kouzel.' }}</h2><p>Odehrál jsi {{ lobby.player.matchesPlayed }} zápasů a získal {{ lobby.player.xp }} XP. {{ lobby.player.xp >= 30 ? 'Hůlka II ti otevírá taktiku. Úder, Lest a Štít teď mají omezenou zásobu.' : 'Na 30 XP se otevře taktika. Zatím poznávej soupeře a dej si odvetu.' }}</p><RouterLink class="aw-button aw-button--primary" to="/play/challenge">{{ lobby.player.xp >= 30 ? 'Vyzkoušet taktiku' : 'Vyzvat soupeře' }}</RouterLink></PaperSurface></template>
        </template>
        <template v-else-if="ranking">
          <p class="game-eyebrow">Společný večer</p><h1 id="game-title">Pořadí večera</h1>
          <PaperSurface class="game-paper"><fieldset class="game-modes"><legend>Disciplína</legend><label><input v-model="rankingDiscipline" type="radio" value="combat" /> Bojová XP</label><label><input v-model="rankingDiscipline" type="radio" value="yolo" /> YOLO výhry</label></fieldset>
            <ol class="game-ranking"><li v-for="row in visibleRankings" :key="row.id" :class="{'game-ranking-own': row.id === account?.id}"><span>{{ row.rank }}.</span><strong>{{ row.displayName }}</strong><span>{{ rankingDiscipline === 'combat' ? `${row.xp} XP` : `${row.wins} výher` }}</span></li></ol>
            <AwButton v-if="!showEntireRanking" @click="showEntireRanking = true">Ukázat celé pořadí</AwButton><p>YOLO nepřidává bojovou sílu.</p>
          </PaperSurface>
        </template>
        <template v-else-if="messages">
          <p class="game-eyebrow">Odhaleno tvou hrou</p><h1 id="game-title">Zprávy z dílny</h1><PaperSurface v-for="message in progress?.messages ?? []" :key="message.id" class="game-paper"><h2>{{ message.title }}</h2><p>{{ message.text }}</p><RouterLink :to="message.destination">{{ message.action }} →</RouterLink></PaperSurface>
        </template>
        <template v-else-if="bossSetup">
          <template v-if="bossProfile?.revealed">
            <p class="game-eyebrow">{{ bossProfile.rescued ? 'Ollivander je volný.' : 'Ollivander je v zajetí.' }}</p><h1 id="game-title">Baron Šarlatán</h1>
            <GameCharacter class="game-boss-portrait" character="boss" />
            <PaperSurface class="game-paper"><h2>{{ bossProfile.rescued ? 'Ještě jednu reklamaci?' : 'Je čas zvednout oponu.' }}</h2><p>6 energie proti 5. Nejvýše 12 výměn, tři čitelné fáze. Hůlku ani XP neztrácíš.</p><p>Obtížnost je zmrazená pro celou skupinu. Referenční síla: {{ bossProfile.referenceXp }} XP. První výhra osvobodí Ollivandera všem.</p><p v-if="lobby.player.xp < 30">S hůlkou I máš neomezená kouzla, žádné vlastní jiskry. Jde o exhibici.</p><p>Nesoutěžní hra · bez fyzické ceny</p>
              <RouterLink v-if="bossProfile.activeAttemptId" class="aw-button aw-button--primary" :to="`/play/boss/${bossProfile.activeAttemptId}`">Pokračovat v pokusu</RouterLink><AwButton v-else variant="primary" :busy="busy" :disabled="uncertain" @click="startBoss">Vyzvat bosse</AwButton>
            </PaperSurface>
          </template>
          <template v-else><h1 id="game-title">Opona zůstává zavřená.</h1><p>Další kapitola večera zatím nebyla odhalena.</p></template>
        </template>
        <template v-else-if="challenge">
          <p class="game-eyebrow">Místo pro dva</p><h1 id="game-title">Koho vyzveš?</h1>
          <PaperSurface class="game-paper"><form @submit.prevent="start">
            <label class="game-field">Soupeř<select v-model="opponentId" required><option v-for="opponent in lobby.opponents" :key="opponent.id" :value="opponent.id">{{ opponent.displayName }} · {{ opponent.xp }} XP</option></select></label>
            <p v-if="!lobby.opponents.length">Zatím není koho vyzvat. Zkus krátký trénink bez XP.</p>
            <fieldset class="game-modes"><legend>Jak se dnes hraje?</legend>
              <label><input v-model="mode" type="radio" value="quick" /> <span><strong>Rychlý duel</strong><small>7 výměn · přibližně 90 sekund</small></span></label>
              <label v-if="lobby.player.tacticalUnlocked"><input v-model="mode" type="radio" value="tactical" :disabled="(selectedOpponent?.xp ?? 0) < 30" /><span><strong>Taktický duel</strong><small>8 výměn · omezená zásoba · oba potřebují 30 XP</small></span></label>
              <label v-if="lobby.player.yoloUnlocked"><input v-model="mode" type="radio" value="yolo" :disabled="(selectedOpponent?.matchesPlayed ?? 0) < 3" /><span><strong>YOLO lóže</strong><small>AI vymyslí příběh i vítěze. Bez bojových XP.</small></span></label>
            </fieldset>
            <p v-if="mode === 'yolo'">OpenAI použije herní jméno, svět, název hůlky a nejvýše tři herní rysy každé postavy.</p>
            <GameConsent v-model:tasks-enabled="tasksEnabled" v-model:adult-consent="adultConsent" v-model:flirt-consent="flirtConsent" />
            <label v-if="mode === 'tactical' && (progress?.tacticalMatches ?? 0) >= 2" class="game-spark"><input v-model="useLoadouts" type="checkbox" /> Chci vlastní sestavy, pokud je známe oba</label>
            <AwButton type="submit" variant="primary" :busy="busy" :disabled="!canStart || uncertain">Poslat výzvu</AwButton>
          </form></PaperSurface>
        </template>
        <template v-else-if="chronicle">
          <p class="game-eyebrow">Tvé skutečné zápasy</p><h1 id="game-title">Moje kronika</h1>
          <PaperSurface class="game-paper"><p v-if="!lobby.history.length">První historka teprve čeká na své kouzlo.</p><ol class="game-match-list"><li v-for="item in lobby.history" :key="item.id"><RouterLink :to="`/play/match/${item.id}`"><span>{{ modeName(item.mode) }}<strong>{{ item.opponent.displayName }}</strong></span><span>{{ item.playerScore }} : {{ item.opponentScore }}<small>+{{ item.xpAwarded }} XP</small></span></RouterLink></li></ol></PaperSurface>
        </template>
        <template v-else>
          <p class="game-eyebrow">Večer má tvůj rukopis.</p><h1 id="game-title">Tvůj tah,<br /><em>{{ lobby.player.displayName }}.</em></h1>
          <PaperSurface v-if="!milestoneDismissed && progress?.pending?.length" class="game-paper game-milestone"><p class="game-eyebrow">Nová kapitola</p><h2>{{ progress.pending[0].title }}</h2><p>{{ progress.pending[0].text }}</p><AwButton variant="primary" :busy="busy" @click="acknowledge(progress.pending[0], true)">{{ progress.pending[0].action }}</AwButton><AwButton @click="milestoneDismissed = true">Pokračovat později</AwButton></PaperSurface>
          <div class="game-lobby-stage game-lobby-stage--table"><img :src="tableScene" alt="" width="853" height="1844" /><p>Hůlka {{ lobby.player.tacticalUnlocked ? 'II' : 'I' }}<span>O osudu rozhodují tři kouzla.<br />O historkách lidé u stolu.</span></p></div>
          <PaperSurface class="game-paper game-progress"><p class="game-eyebrow">{{ nextMilestone }}</p><div class="game-progress-track" role="progressbar" :aria-valuenow="Math.min(lobby.player.xp, 30)" :aria-valuemax="30" :aria-valuemin="0" aria-label="Postup k hůlce II"><span :style="{ width: `${Math.min(lobby.player.xp / 30, 1) * 100}%` }" /></div>
            <p>{{ lobby.player.matchesPlayed }} dokončených zápasů · {{ lobby.player.xp }} XP</p>
            <RouterLink class="aw-button aw-button--primary" to="/play/challenge">{{ lobby.opponents.length ? 'Vybrat soupeře' : 'Zatím není koho vyzvat' }}</RouterLink><RouterLink class="game-secondary-link" to="/play/practice">Krátký trénink bez XP</RouterLink>
          </PaperSurface>
          <section v-if="lobby.active.length || lobby.incoming.length" class="game-open-matches"><h2>U tvého stolu</h2><RouterLink v-for="item in [...lobby.active, ...lobby.incoming]" :key="item.id" class="game-match-link" :to="`/play/match/${item.id}`"><span>{{ item.opponent.displayName }}<small>{{ modeName(item.mode) }}</small></span><span>{{ item.status === 'active' ? 'Pokračovat →' : 'Výzva →' }}</span></RouterLink></section>
          <div v-if="lobby.player.yoloUnlocked" class="game-commentator-invite"><GameCharacter character="commentator" /><div><p class="game-eyebrow">Bohumil Křivopísk</p><h2>Pravidla? Ta se poddají.</h2><p>Rozhodčí bez licence tě zve do YOLO lóže. Historku i vítěze vymýšlí AI.</p><RouterLink :to="{path: '/play/challenge', query: {mode: 'yolo'}}">Vstoupit do lóže →</RouterLink></div></div>
          <p v-if="progress?.titles?.length">Tvé tituly: {{ progress.titles.join(' · ') }}</p>
          <nav class="game-side-links" aria-label="Další cesty"><RouterLink to="/play/chronicle">Moje kronika →</RouterLink><RouterLink to="/play/ranking">Pořadí večera →</RouterLink><RouterLink v-if="lobby.player.tacticalUnlocked" to="/play/keeper">{{ progress?.keeperState === 'captured' ? 'Dílnu převzal Baron →' : 'Navštívit Ollivandera →' }}</RouterLink><RouterLink v-if="progress?.messages?.length" to="/play/messages">Zprávy z dílny →</RouterLink><RouterLink v-if="(progress?.tacticalMatches ?? 0) >= 3" to="/play/teams">Najít spojence →</RouterLink><RouterLink v-if="lobby.boss?.revealed" to="/play/boss">{{ lobby.boss.rescued ? 'Odveta s Baronem' : 'Ollivander je v zajetí. Vyzvat bosse →' }}</RouterLink></nav>
        </template>
      </template>
    </div>
  </SceneShell>
</template>
