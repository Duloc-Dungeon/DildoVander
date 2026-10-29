<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import SceneShell from '../../components/scene/SceneShell.vue';
import PaperSurface from '../../components/scene/PaperSurface.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';

import amberWand from '../../assets/wns-v2/art/wand-amber.png';
import GameCharacter from './GameCharacter.vue';
import GameBossStage from './GameBossStage.vue';
import GameBossEnergy from './GameBossEnergy.vue';
import GameLoadouts from './GameLoadouts.vue';
import GameRanking from './GameRanking.vue';
import GameRules from './GameRules.vue';
import GameSpellToken from './GameSpellToken.vue';
import GameClash from './GameClash.vue';
import GameYoloStage from './GameYoloStage.vue';
import { explainRound, duelHighlight } from './duelExplanation';
import { useGameWandArtwork } from './useGameWandArtwork';
import { useGameLiveUpdates } from './useGameLiveUpdates';
import { useOpponentWandArtwork } from './useOpponentWandArtwork';
import { account, accountRequest } from '../identity/session';
import { actions, modeName, gameRequest, gameErrorText, GameError, operation, practiceRound,
  type Choice, type GameMode, type Lobby, type Match, type Round, type BossProfile, type Progress, type Milestone } from './game';
import './game.css';
import './boss.css';
import './duel.css';
import SoundToggle from '../../application/SoundToggle.vue';
import { wnsSound } from '../../application/wnsSound';

const route = useRoute();
const router = useRouter();
const lobby = ref<Lobby>();
interface GameWandStatus { hasCharacter: boolean; wand: { wandId?: string } | null; tier: number; characterDisplayName?: string | null }
const wandStatus = ref<GameWandStatus>();
const playerWand = useGameWandArtwork();
const opponentWand = useOpponentWandArtwork();
const bossProfile = ref<BossProfile>();
const progress = ref<Progress>();
const progressError = ref('');
const milestoneDismissed = ref(false);
const match = ref<Match>();
const labeledPlayerName = ref<string | null>(null);
const busy = ref(false);
const loading = ref(true);
const error = ref('');
const notice = ref('');
const uncertain = ref(false);
const opponentId = ref(typeof route.query.opponent === 'string' ? route.query.opponent : '');
const mode = ref<GameMode>(route.query.mode === 'yolo' ? 'yolo' : route.query.mode === 'tactical' ? 'tactical' : 'quick');
const useLoadouts = ref(false);
const practiceSkipped = ref(false);
const selected = ref<Choice>();
const spark = ref(false);
const yoloRevealed = ref(false);
const practice = computed(() => route.path === '/play/practice');
const chronicle = computed(() => route.path === '/play/chronicle');
const challenge = computed(() => route.path === '/play/challenge');
const bossSetup = computed(() => route.path === '/play/boss');
const bossAttempt = computed(() => route.path.startsWith('/play/boss/'));
const keeper = computed(() => route.path === '/play/keeper');
const ranking = computed(() => route.path === '/play/ranking');
const messages = computed(() => route.path === '/play/messages');
const readOnly = computed(() => ranking.value || messages.value || chronicle.value);
const matchId = computed(() => typeof route.params.id === 'string' ? route.params.id : '');
const practiceRounds = ref<Round[]>([]);
const playerScore = computed(() => practice.value ? practiceRounds.value.filter(round => round.winner === 'player').length : match.value?.playerScore ?? 0);
const opponentScore = computed(() => practice.value ? practiceRounds.value.filter(round => round.winner === 'opponent').length : match.value?.opponentScore ?? 0);
const practiceDone = computed(() => practiceSkipped.value || practiceRounds.value.length >= 3);
const recentRounds = computed(() => (practice.value ? practiceRounds.value : match.value?.rounds ?? []).slice(-3));
const resultHighlight = computed(() => duelHighlight(match.value?.rounds ?? practiceRounds.value));
const practicePrompt = computed(() => [
  'Nejdřív štít: cvičný mág vyšle úder. Vyber Štít a sleduj, proč ho zastaví.',
  'Teď odpověz na štít. Které kouzlo ho obejde? Vyber svůj tah; odhalení ti vysvětlí výsledek.',
  'Poslední výměna je tvoje. Soupeřův tah už neznáš. Úder přeruší lest, lest obejde štít, štít zastaví úder.',
][practiceRounds.value.length] ?? 'Tři výměny za tebou. Pravidla můžeš kdykoli otevřít znovu.');
const completed = computed(() => practice.value ? practiceDone.value : match.value?.status === 'complete');
const tableDuel = computed(() => practice.value || Boolean(match.value && match.value.mode !== 'yolo'
  && (match.value.status === 'active' || match.value.status === 'complete')));
const playerDuel = computed(() => tableDuel.value && (match.value?.mode === 'quick' || match.value?.mode === 'tactical'));
const theatricalDuel = computed(() => practice.value || playerDuel.value);
const duelResult = computed(() => {
  if (!completed.value) return undefined;
  if (!practice.value) return match.value?.result;
  return playerScore.value === opponentScore.value ? 'draw' : playerScore.value > opponentScore.value ? 'win' : 'loss';
});
const roundCount = computed(() => practice.value ? 3 : match.value?.totalRounds ?? 0);
const completedRoundCount = computed(() => practice.value ? practiceRounds.value.length : match.value?.completedRounds ?? 0);
const playerName = computed(() => account.value?.roles?.includes('Owner') ? 'Olivander'
  : labeledPlayerName.value || account.value?.displayName || 'Ty');
const opponentName = computed(() => practice.value ? 'Cvičný mág' : match.value?.opponent.displayName || 'Soupeř');
const selectedAction = computed(() => actions.find(action => action.id === selected.value));
const lastRound = computed(() => practice.value ? practiceRounds.value.at(-1) : match.value?.rounds.at(-1));
const duelRoundTitle = computed(() => {
  switch (lastRound.value?.winner) {
    case 'player': return 'Tvoje výměna.';
    case 'opponent': return 'Soupeřova výměna.';
    case 'draw': return 'Nerozhodná výměna.';
    default: return 'Poslední odhalení';
  }
});
const pendingTasks = computed(() => lobby.value?.history.filter(item => item.partyTask?.status === 'pending') ?? []);
const canReviewTask = computed(() => match.value?.status === 'complete' && Boolean(match.value.partyTask));
const selectedOpponent = computed(() => lobby.value?.opponents.find(opponent => opponent.id === opponentId.value));
const readyOpponents = computed(() => lobby.value?.opponents.filter(opponent => opponent.hasWand !== false) ?? []);
const opponentTacticalReady = computed(() => selectedOpponent.value?.tacticalUnlocked ?? (selectedOpponent.value?.xp ?? 0) >= 30);
const advanced = computed(() => mode.value === 'tactical' && lobby.value?.player.tacticalUnlocked && opponentTacticalReady.value);
const canStart = computed(() => Boolean(selectedOpponent.value) && selectedOpponent.value?.hasWand !== false && (mode.value === 'quick' || mode.value === 'tactical' && advanced.value || mode.value === 'yolo' && lobby.value?.player.yoloUnlocked && (selectedOpponent.value?.matchesPlayed ?? 0) >= 3));
const nextMilestone = computed(() => {
  const player = lobby.value?.player;
  if (!player) return '';
  if (player.matchesPlayed < 2) return 'Dva dokončené duely odhalí jiskry.';
  if (player.xp < 30) return `${player.xp} / 30 XP · Hůlka II je blízko`;
  if ((wandStatus.value?.tier ?? 0) < 2) return '30 XP máš. Hůlku II si teď vyrob v dílně.';
  return 'Hůlka II · Teď plánuješ zásobu kouzel';
});
const resultTitle = computed(() => {
  if (match.value?.mode === 'boss') return match.value.result === 'win' ? 'Baron padl. Opona se zvedá.' : 'Tentokrát vyhrál Baron.';
  const result = match.value?.result ?? (playerScore.value === opponentScore.value ? 'draw' : playerScore.value > opponentScore.value ? 'win' : 'loss');
  if (result === 'draw') return 'Remíza. Magie váhá.';
  return result === 'win' ? 'Tohle bylo tvoje.' : 'Dnes slaví soupeř.';
});
const liveMessage = computed(() => {
  if (match.value?.mode === 'yolo') {
    if (uncertain.value) return 'Stav přenosu ověřujeme se serverem.';
    return match.value.status === 'complete' && match.value.yolo?.status === 'complete'
      ? 'Příběh je připraven. Dějství odhalíš tlačítkem Další část.' : '';
  }
  if (busy.value) return 'Potvrzujeme tah…';
  if (uncertain.value) return 'Výsledek odeslání ověřujeme se serverem.';
  if (match.value?.ownChoiceLocked) return 'Tvůj tah je uzamčený. Čekáme na soupeře.';
  if (completed.value) return resultTitle.value;
  return '';
});
let controller = new AbortController();
let generation = 0;
const refreshing = ref(false);
let timer: ReturnType<typeof setInterval> | undefined;

function updateMatch(value: Match) {
  if (matchId.value && value.id !== matchId.value) return;
  if (match.value?.id === value.id && value.revision < match.value.revision) return;
  if (match.value?.id !== value.id || match.value?.completedRounds !== value.completedRounds) {
    selected.value = undefined; spark.value = false;
  }
  if (match.value?.id !== value.id) yoloRevealed.value = false;
  match.value = value;
  void opponentWand.refresh(value);
}
async function refresh() {
  if (refreshing.value || busy.value) return;
  refreshing.value = true;
  const token = generation;
  try {
    const currentWand = await accountRequest<GameWandStatus>('/app/v1/customer/wand', 'GET', undefined, controller.signal);
    if (token !== generation) return;
    labeledPlayerName.value = currentWand.characterDisplayName ?? null;
    wandStatus.value = currentWand;
    if (practice.value || matchId.value) void playerWand.refresh(currentWand.wand?.wandId);
    else playerWand.clear();
    if ((!currentWand.wand && !bossSetup.value && !bossAttempt.value && !readOnly.value) || practice.value) {
      if (!currentWand.wand && matchId.value) {
        const current = await gameRequest<Match>(`${bossAttempt.value ? '/boss/attempts' : '/matches'}/${encodeURIComponent(matchId.value)}`, undefined, controller.signal);
        if (token === generation) updateMatch(current);
      } else if (!currentWand.wand && !practice.value) {
        const currentLobby = await gameRequest<Lobby>('', undefined, controller.signal);
        if (token === generation) lobby.value = currentLobby;
      }
      error.value = ''; uncertain.value = false; return;
    }
    if (matchId.value) {
      const value = await gameRequest<Match>(`${bossAttempt.value ? '/boss/attempts' : '/matches'}/${encodeURIComponent(matchId.value)}`, undefined, controller.signal);
      if (token === generation) updateMatch(value);
    } else {
      const value = await gameRequest<Lobby>('', undefined, controller.signal);
      let nextProgress: Progress | undefined;
      let nextProgressError = '';
      try {
        nextProgress = await gameRequest<Progress>('/progress', undefined, controller.signal);
      } catch {
        nextProgressError = 'Pořadí a postup se nepodařilo obnovit. Zkus obnovit stav.';
      }
      const nextBoss = bossSetup.value ? await gameRequest<BossProfile>('/boss', undefined, controller.signal) : undefined;
      if (token === generation) {
        lobby.value = value;
        progressError.value = nextProgressError;
        if (nextProgress?.pending) progress.value = nextProgress;
        if (nextBoss) bossProfile.value = nextBoss;
        if (!opponentId.value || !value.opponents.some(opponent => opponent.id === opponentId.value && opponent.hasWand !== false)) opponentId.value = value.opponents.find(opponent => opponent.hasWand !== false)?.id ?? '';
        const resume = value.active.find(item => item.status === 'active');
        if (loading.value && route.path === '/play' && route.query.browse !== '1' && resume) void router.replace(`/play/match/${resume.id}`);
        else if (loading.value && route.path === '/play' && route.query.browse !== '1' && value.boss?.activeAttemptId) void router.replace(`/play/boss/${value.boss.activeAttemptId}`);
      }
    }
    if (token === generation) { error.value = ''; uncertain.value = false; }
  } catch (cause) {
    handleRefreshError(cause, token);
  } finally {
    if (token === generation) { refreshing.value = false; loading.value = false; }
  }
}
function handleRefreshError(cause: unknown, token: number) {
  if (token === generation && !controller.signal.aborted) {
    error.value = gameErrorText(cause);
    if (cause instanceof GameError && [401, 403, 404].includes(cause.status)) {
      match.value = undefined; lobby.value = undefined; progress.value = undefined; bossProfile.value = undefined;
      playerWand.clear(); opponentWand.clear();
    }
  }
}
async function refreshLive() {
  if (!matchId.value) return refresh();
  if (refreshing.value || busy.value) return;
  refreshing.value = true;
  const token = generation;
  try {
    const value = await gameRequest<Match>(`${bossAttempt.value ? '/boss/attempts' : '/matches'}/${encodeURIComponent(matchId.value)}`, undefined, controller.signal);
    if (token === generation) {
      updateMatch(value);
      error.value = ''; uncertain.value = false;
    }
  } catch (cause) { handleRefreshError(cause, token); }
  finally { if (token === generation) refreshing.value = false; }
}
useGameLiveUpdates(refreshLive, () => busy.value || refreshing.value);
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
  if (current.id !== matchId.value) { busy.value = false; return; }
  try {
    const value = await gameRequest<Match>(`${current.mode === 'boss' ? '/boss/attempts' : '/matches'}/${current.id}/${action}`, {
      operationId: operation(), expectedRevision: current.revision, ...fields,
    }, controller.signal);
    if (token === generation) {
      updateMatch(value);
      if (action === 'choose') wnsSound.local('confirm');
    }
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
    const value = await gameRequest<Match>('/matches', { opponentId: opponentId.value, mode: mode.value, operationId: operation(), useLoadouts: useLoadouts.value }, controller.signal);
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
  const opponent = practiceRounds.value.length === 0 ? 'strike' : practiceRounds.value.length === 1 ? 'shield' : actions[draw[0] % 3].id;
  const result = practiceRound(selected.value, opponent);
  practiceRounds.value.push({ ...result, index: practiceRounds.value.length + 1 });
  wnsSound.local('confirm');
  selected.value = undefined;
}
function resetPractice() { practiceRounds.value = []; selected.value = undefined; practiceSkipped.value = false; }
function roundText(round: Round) { return explainRound(round); }
function available(choice: Choice) {
  if (practice.value) return practiceRounds.value.length !== 0 || choice === 'shield';
  return match.value?.mode !== 'tactical' && match.value?.mode !== 'boss' || (match.value?.playerInventory?.[choice] ?? 2) > 0;
}
function onVisible() { if (!document.hidden) void refresh(); }
watch(() => route.fullPath, () => {
  playerWand.clear();
  opponentWand.clear();
  generation++; controller.abort(); controller = new AbortController();
  match.value = undefined; selected.value = undefined;
  error.value = ''; notice.value = ''; progressError.value = ''; bossProfile.value = undefined;
  opponentId.value = typeof route.query.opponent === 'string' ? route.query.opponent : '';
  mode.value = route.query.mode === 'yolo' ? 'yolo' : route.query.mode === 'tactical' ? 'tactical' : 'quick';
  useLoadouts.value = false; milestoneDismissed.value = false;
  busy.value = false; uncertain.value = false; loading.value = true; refreshing.value = false;
  void refresh();
});
watch(() => account.value?.id, (value, previous) => {
  if (value === previous) return;
  generation++; controller.abort(); controller = new AbortController(); wandStatus.value = undefined;
  match.value = undefined; lobby.value = undefined; bossProfile.value = undefined; progress.value = undefined; refreshing.value = false;
  error.value = ''; notice.value = ''; progressError.value = '';
  labeledPlayerName.value = null;
  busy.value = false; uncertain.value = false; selected.value = undefined;

  void refresh();
});
onMounted(() => {
  void refresh(); timer = setInterval(() => { if (!document.hidden) void refreshLive(); }, 3000);
  document.addEventListener('visibilitychange', onVisible); window.addEventListener('online', onVisible);
});
onBeforeUnmount(() => {
  generation++; controller.abort(); if (timer) clearInterval(timer);
  document.removeEventListener('visibilitychange', onVisible); window.removeEventListener('online', onVisible);
});
</script>

<template>
  <SceneShell class="game-scene game-experience"
    :class="{ 'responsive-table': tableDuel, 'game-duel-scene': theatricalDuel, 'game-practice-scene': practice, 'game-yolo-scene': match?.mode === 'yolo', 'game-boss-scene': bossSetup && bossProfile?.revealed || match?.mode === 'boss' }"
    :data-duel-result="theatricalDuel ? duelResult : undefined"
    :data-boss-phase="match?.mode === 'boss' ? match.phase : undefined" labelled-by="game-title">
    <div v-if="bossSetup && bossProfile?.revealed || match?.mode === 'boss'" :key="match?.phase" class="boss-scene-light" aria-hidden="true" />
    <div class="game-content">
      <header class="game-header">
        <SoundToggle />
        <RouterLink :to="route.path === '/play' ? '/me/character' : { path: '/play', query: { browse: '1' } }">← {{ route.path === '/play' ? 'Moje postava' : 'Ke hře' }}</RouterLink>
        <span v-if="tableDuel" class="game-eyebrow">{{ practice ? 'Trénink · bez XP' : modeName(match!.mode) }}</span>
        <GameRules v-if="tableDuel" :practice="practice" :tactical="match?.mode === 'tactical'" />
      </header>
      <div v-if="error" class="game-feedback"><AwNotice tone="danger">{{ error }}</AwNotice><AwButton :busy="loading" @click="refresh">Obnovit stav</AwButton></div>
      <p v-if="loading && !match && !lobby && !practice" role="status">Připravujeme tvé místo u stolu…</p>
      <p class="game-sr" role="status" aria-live="polite">{{ liveMessage }}</p>

      <template v-if="!wandStatus">
        <p v-if="!loading" role="status">Nejdřív potřebujeme ověřit tvoji hůlku. Obnov stav a zkus to znovu.</p>
      </template>
      <template v-else-if="!wandStatus.wand && !canReviewTask && !bossSetup && !bossAttempt && !readOnly">
        <h1 id="game-title">Nejdřív vlastní hůlku.</h1>
        <AwButton v-if="match && ['incoming', 'active', 'preparing', 'generating', 'failed'].includes(match.status)" :busy="busy" @click="mutate(match.canAccept ? 'decline' : 'cancel')">Zrušit rozehranou hru</AwButton>
        <section class="game-workshop-invite" aria-label="Příprava ke hře">
          <div class="game-workshop-invite__art"><img :src="amberWand" alt="" width="240" height="240" /></div>
          <div class="game-workshop-invite__content">
            <template v-if="lobby?.boss?.revealed && !lobby.boss.rescued">
              <p>Dílna je v zajetí Barona. Můžeš ho vyzvat i bez první hůlky; pro záchranu dostaneš exhibiční kouzla.</p>
              <RouterLink class="aw-button aw-button--primary" to="/play/boss">Osvobodit dílnu</RouterLink>
            </template>
            <template v-else-if="wandStatus.hasCharacter">
              <p>Magický kufr už zná tvoji postavu. Teď spolu vymyslíte její hůlku. Můžeš mluvit nebo psát.</p>
              <RouterLink class="aw-button aw-button--primary" to="/play/wand">Vstoupit do kufrovy dílny</RouterLink>
            </template>
            <template v-else>
              <p>Pro výrobu hůlky nejdřív ulož svoji postavu.</p>
              <RouterLink class="aw-button aw-button--primary" to="/me/character">Připravit postavu</RouterLink>
            </template>
          </div>
        </section>
        <section v-if="pendingTasks.length" class="game-open-matches"><h2>Úkoly k potvrzení</h2><RouterLink v-for="item in pendingTasks" :key="item.id" class="game-match-link" :to="`/play/match/${item.id}`"><span>{{ item.partyTask!.title }}<small>Po duelu s {{ item.opponent.displayName }}</small></span><span>Potvrdit výsledek →</span></RouterLink></section>
        <nav class="game-side-links" aria-label="Další hry a výsledky"><RouterLink to="/play/pod-peceti">Pod pečetí</RouterLink><RouterLink to="/play/ranking">Žebříček</RouterLink><RouterLink to="/play/chronicle">Moje kronika</RouterLink></nav>
      </template>
      <template v-else-if="practice || match">
        <div class="game-match-heading" :class="{ 'game-sr': tableDuel }">
          <p v-if="match?.mode !== 'yolo'" class="game-eyebrow">{{ practice ? 'Trénink · bez XP' : modeName(match!.mode) }}</p>
          <h1 id="game-title">{{ practice ? 'Zkus kouzlo.' : match!.mode === 'yolo' ? 'YOLO lóže' : match!.mode === 'boss' ? 'Zápas s bossem' : match!.opponent.displayName }}</h1>
        </div>
        <PaperSurface v-if="!practice && match?.status === 'incoming'" class="game-paper">
          <p class="game-eyebrow">Výzva u stolu</p>
          <h2>{{ modeName(match.mode) }}</h2>
          <p>{{ match.mode === 'tactical' ? 'Nejvýše osm výměn, omezená zásoba kouzel. Duel končí, jakmile už výsledek nelze zvrátit.' : match.mode === 'yolo' ? 'AI vytvoří scénář a možnosti z obou postav a hůlek. Každý třikrát rozhodne; pak AI vyhodnotí uložený průběh. Bez bojových XP.' : 'Nejvýše sedm tajných výměn. Jakmile už soupeř nemůže výsledek zvrátit, duel končí.' }}</p>
          <p v-if="match.mode === 'yolo'">Příběh použije herní jméno, svět, název hůlky a nejvýše tři herní rysy každé postavy.</p>
          <p>Po prohře se zobrazí úkol podle pravidel skupiny. Potvrdíš, zda byl splněn.</p>
          <label v-if="match.canAccept && match.loadoutsAvailable" class="game-spark"><input v-model="useLoadouts" type="checkbox" /> Chci v tomto zápase volit vlastní sestavu</label>
          <div class="game-stack">
            <AwButton v-if="match.canAccept" variant="primary" :busy="busy" @click="mutate('accept', { useLoadouts })">Přijmout výzvu</AwButton>
            <AwButton v-if="match.canAccept" :busy="busy" @click="mutate('decline')">Odmítnout</AwButton>
            <p v-if="match.canCancel" role="status">Výzva odeslána. Čekáme na souhlas soupeře.</p>
            <AwButton v-if="match.canCancel" :busy="busy" @click="mutate('cancel')">Zrušit výzvu</AwButton>
          </div>
        </PaperSurface>

        <PaperSurface v-else-if="match?.needsLoadout" class="game-paper"><p v-if="match.completedRounds === 4" role="status">Čtyři výměny za námi. Pro druhý blok vyber novou zásobu kouzel.</p><GameLoadouts :key="match.block" :busy="busy" :uncertain="uncertain" :locked="match.ownLoadoutLocked" @confirm="loadout => mutate('loadout', { loadout })" /></PaperSurface>
        <template v-else-if="practice || match?.status === 'active' || completed || match?.mode === 'yolo'">
          <GameYoloStage v-if="match?.mode === 'yolo'" :key="match.id" :match="match" :player-name="playerName"
            :wand-url="playerWand.url.value" :wand-name="playerWand.name.value" :synthetic="playerWand.synthetic.value"
            :opponent-wand-url="opponentWand.url.value" :busy="busy" :uncertain="uncertain"
            @retry="mutate('retry')" @cancel="mutate('cancel')" @choose="choice => mutate('choose', { choice })" @revealed="yoloRevealed = $event" />
          <template v-else>
            <section v-if="practice && !completed" class="duel-practice" aria-label="Průvodce tréninkem">
              <h2>Tři výměny nanečisto</h2><p>{{ practicePrompt }}</p>
              <AwButton @click="practiceSkipped = true">Přeskočit trénink</AwButton>
            </section>
            <template v-if="match?.mode === 'boss'">
              <GameBossStage :phase="match.phase" :expression="match.result === 'win' ? 2 : (match.opponentEnergy ?? 5) <= 2 ? 1 : 0">
                <h2 class="boss-identity">Baron Šarlatán</h2><p>Poraz Barona do 12 výměn.</p>
                <p v-if="match.phase" class="boss-phase" aria-live="polite">Fáze {{ match.phase }} ze 3</p>
                <ol v-if="match.phase" class="boss-phases" aria-label="Fáze souboje">
                  <li v-for="phase in 3" :key="phase" :aria-current="phase === match.phase ? 'step' : undefined" :aria-label="`Fáze ${phase}`" />
                </ol>
              </GameBossStage>
              <GameBossEnergy :player-energy="match.playerEnergy" :opponent-energy="match.opponentEnergy" :exhibition="match.exhibition" />
              <div class="boss-tactics">
                <p v-if="match.hint">{{ match.hint }}</p>
                <p v-if="match.bossSpark" class="boss-spark">Baron v této výměně přisazuje jiskru.</p>
                <p class="boss-strength">Síla tvé hůlky: {{ match.strength?.toLocaleString('cs-CZ', { maximumFractionDigits: 2 }) }}×</p>
              </div>
            </template>
            <div v-else class="game-score game-score--duel" aria-label="Skóre zápasu">
              <span class="game-player-avatar" aria-hidden="true">{{ playerName.slice(0, 1) }}</span>
              <div><span>{{ playerName }}</span><strong :key="`player-${playerScore}`">{{ playerScore }}</strong></div>
              <span class="game-score-divider" aria-hidden="true">:</span>
              <div><span>{{ opponentName }}</span><strong :key="`opponent-${opponentScore}`">{{ opponentScore }}</strong></div>
              <span class="game-player-avatar game-player-avatar--opponent" aria-hidden="true">{{ opponentName.slice(0, 1) }}</span>
            </div>
            <div :class="{ 'boss-round': match?.mode === 'boss', 'duel-round': theatricalDuel }">
              <p class="game-round-label">{{ completed ? 'Dohráno' : `Výměna ${Math.min((practice ? practiceRounds.length : match!.completedRounds) + 1, practice ? 3 : match!.totalRounds)} ze ${practice ? 3 : match!.totalRounds}` }}<span v-if="match?.mode === 'tactical'"> · blok {{ match.block }}</span></p>
              <div class="game-round-dots" aria-hidden="true"><span v-for="round in roundCount" :key="round" :class="{ done: round <= completedRoundCount, current: !completed && round === completedRoundCount + 1 }" /></div>
            </div>
            <div v-if="match?.mode !== 'boss' || lastRound" class="game-table-stage" :class="{ 'game-table-stage--complete': completed }">
            <GameClash v-if="match?.mode !== 'boss'" :key="practice ? 'practice' : match?.id" :theatrical="theatricalDuel" :round="lastRound" :locked="match?.ownChoiceLocked" :wand-url="playerWand.url.value" :wand-name="playerWand.name.value" :synthetic="playerWand.synthetic.value" :practice-opponent="practice" :opponent-wand-url="opponentWand.url.value" :opponent-wand-name="match?.opponent.wandName ?? undefined" :opponent-synthetic="match?.opponent.wandSynthetic" />
            <PaperSurface v-if="lastRound" class="game-paper game-reveal" :key="completedRoundCount" aria-live="polite" aria-atomic="true">
              <p :class="theatricalDuel ? 'duel-round-verdict' : 'game-eyebrow'" :data-winner="theatricalDuel ? lastRound.winner : undefined">{{ theatricalDuel ? duelRoundTitle : 'Poslední odhalení' }}</p>
              <p v-if="lastRound">{{ roundText(lastRound) }}</p>
              <p v-else>Úder přeruší lest. Lest obejde štít. Štít zastaví úder. Oba hráči volí tajně.</p>
            </PaperSurface>
            </div>
            <PaperSurface v-if="completed" class="game-paper game-result">
<h2>{{ practice ? practiceSkipped ? 'Trénink přeskočen' : 'Trénink dokončen' : resultTitle }}</h2>
              <p v-if="!practice && resultHighlight" class="duel-highlight">{{ resultHighlight }}</p>
              <p v-if="match?.mode === 'boss' && match.result === 'win'">Baron poražen po {{ match.completedRounds }} výměnách. Dílna je zase přístupná.</p>
              <p v-if="!practice" class="game-xp">+{{ match!.xpAwarded }} XP</p><p v-else>Trénink nepřidává XP ani úkoly. Pravidla i trénink můžeš znovu otevřít od stolu.</p>
              <p v-if="!practice && match?.mode !== 'boss'">V odvetě se zásoby i skóre počítají od začátku. Teď už víš, co padlo v téhle hře.</p>
              <RouterLink v-if="!practice && match?.partyTask?.status !== 'pending'" class="aw-button aw-button--primary" :to="match?.mode === 'boss' ? '/play/boss' : { path: '/play/challenge', query: { opponent: match!.opponent.id, mode: match!.mode } }">Odveta</RouterLink>
              <AwButton v-if="practice" variant="primary" @click="resetPractice">Znovu otevřít trénink</AwButton>
              <p v-else-if="match?.partyTask?.status === 'pending'">Ještě potvrď výsledek úkolu níže.</p>
              <RouterLink to="/play">Zpět ke stolu</RouterLink>
            </PaperSurface>
            <section v-if="match?.mode === 'tactical' || match?.mode === 'boss'" class="duel-stocks" aria-label="Zásoby obou hráčů">
              <h2>Zásoba kouzel</h2>
              <p v-if="match.mode === 'boss'">Každé kouzlo má dvě použití. Po čtyřech výměnách dostaneš novou zásobu. {{ match.exhibition ? 'Cvičný pokus je bez jisker.' : 'Jiskru přisazuješ zvlášť pod volbou kouzla.' }}</p>
              <table><caption>Každý tah spotřebuje jedno kouzlo, i při remíze.</caption><thead><tr><th scope="col">Kouzlo</th><th scope="col">Ty</th><th scope="col">{{ match.opponent.displayName }}</th></tr></thead><tbody><tr v-for="action in actions" :key="action.id"><th scope="row">{{ action.name }}</th><td>{{ match.playerInventory?.[action.id] ?? '—' }}</td><td>{{ match.opponentInventory?.[action.id] ?? '—' }}</td></tr></tbody></table>
              <p v-if="match.mode === 'tactical' && match.completedRounds === 4" role="status">Čtyři výměny za námi. Začíná druhý blok s novou zásobou kouzel.</p>
            </section>
            <section v-if="!completed && recentRounds.length" class="duel-recent" aria-label="Poslední tři odhalené výměny"><h2>Co už soupeř zahrál</h2><ol><li v-for="round in recentRounds" :key="round.index"><strong>{{ round.index }}.</strong> {{ roundText(round) }}</li></ol></section>
            <PaperSurface v-if="!completed" class="game-paper game-controls">
              <template v-if="!match?.ownChoiceLocked">
                <fieldset class="game-spells"><legend>Vyber kouzlo</legend>
                  <label v-for="action in actions" :key="action.id" :class="{ selected: selected === action.id, exhausted: !available(action.id) }">
                    <input v-model="selected" type="radio" name="spell" :value="action.id" :disabled="busy || uncertain || !available(action.id)" @change="wnsSound.local('select')" />
                    <GameSpellToken :action="action.id" /><strong>{{ action.name }}</strong><small>{{ action.beats }}</small>
                    <span v-if="match?.mode === 'tactical' || match?.mode === 'boss'">{{ available(action.id) ? `${match.playerInventory?.[action.id] ?? 2} použití` : 'Vyčerpáno' }}</span>
                  </label>
                </fieldset>
                <p class="game-choice-explanation" aria-live="polite">{{ selectedAction ? `${selectedAction.name}. ${selectedAction.beats}.` : 'Vyber kouzlo. Tah odešleš až potvrzením.' }}</p>
                <label v-if="!practice && (match!.sparksEnabled || match!.playerSparks > 0)" class="game-spark"><input v-model="spark" type="checkbox" :disabled="!match!.playerSparks || busy || uncertain" /><span>{{ match!.playerSparks ? `Přisadit jiskru · zbývají ${match!.playerSparks}` : 'Jiskry vyčerpány' }}<small>{{ match!.mode === 'boss' ? 'Jiskra se spotřebuje hned. Zvyšuje zásah vítěze, tvoje síla ho násobí. Při remíze se energie nemění.' : 'Jiskra se spotřebuje hned. Vítěz získá 1 bod za výměnu a body za jiskry obou hráčů. Při remíze nezíská bod nikdo.' }}</small></span><span class="game-spark-marks" aria-hidden="true"><span v-for="mark in match!.playerSparks" :key="mark" :class="{ 'game-spark-mark--bid': spark && mark === 1 }">✦</span></span></label>
                <AwButton variant="primary" :busy="busy" :disabled="!selected || uncertain" @click="submitChoice">Potvrdit tah</AwButton>
                <p v-if="match?.opponentChoiceLocked" class="game-helper">Soupeř je připraven. Jeho tah zůstává tajný.</p>
              </template>
              <p v-else-if="match?.mode === 'boss'" class="game-helper">Tvůj tah je uzamčený. Čekáme na soupeře.</p>
            </PaperSurface>
          </template>

          <AwButton v-if="match?.mode === 'boss' && match.status === 'active'" class="game-exit" :busy="busy" @click="mutate('cancel')">Ukončit pokus bez výsledku</AwButton>
          <AwButton v-if="match?.canCancel && match.status === 'active' && !['boss', 'yolo'].includes(match.mode)" class="game-exit" :busy="busy" @click="mutate('cancel')">Ukončit zápas bez výsledku</AwButton>
          <PaperSurface v-if="match?.partyTask && (match.mode !== 'yolo' || yoloRevealed)" class="game-paper game-task">
            <p class="game-eyebrow">Úkol po prohře · {{ match.partyTask.category === 'classic' ? 'Klasika' : match.partyTask.category === 'adult' ? 'Dvojsmysly 18+' : 'Flirt' }}</p><h2>{{ match.partyTask.title }}</h2><p>{{ match.partyTask.text }}</p>
            <template v-if="match.result === 'loss' && match.partyTask.status === 'pending'">
              <p>Splněno uloží dokončení úkolu. Přeskočit uloží, že úkol nebyl splněn, a zpřístupní odvetu.</p>
              <div class="game-task-outcome">
                <AwButton variant="primary" :busy="busy" :disabled="uncertain" @click="mutate('task', { taskAction: 'done' })">Splněno</AwButton>
                <AwButton :busy="busy" :disabled="uncertain" @click="mutate('task', { taskAction: 'skip' })">Přeskočit úkol</AwButton>
              </div>
            </template>
            <p v-else role="status">{{ match.partyTask.status === 'done' ? 'Uloženo: úkol splněn.' : 'Uloženo: úkol nesplněn.' }}</p>
          </PaperSurface>
          <section v-if="match?.mode === 'tactical' && completed && match.combatNarration" class="duel-narration" aria-live="polite"><h2>Jak se o tom bude vyprávět</h2><p v-if="match.combatNarration.status === 'complete' && match.combatNarration.text">{{ match.combatNarration.text }}</p><p v-else-if="['pending', 'generating'].includes(match.combatNarration.status)">Vyprávění se připravuje. XP už jsou uložené a můžeš na odvetu.</p><p v-else>Vyprávění se nepodařilo připravit. Výsledek i XP zůstávají uložené.</p></section>
          <details v-if="lastRound" class="game-history"><summary>Průběh zápasu</summary><ol><li v-for="round in practice ? practiceRounds : match!.rounds" :key="round.index">{{ roundText(round) }}</li></ol></details>
        </template>
        <PaperSurface v-else class="game-paper"><h2>Zápas byl {{ match?.status === 'declined' ? 'odmítnut' : 'ukončen' }}.</h2><p>Bez vítěze a bez XP. Můžeš poslat novou výzvu.</p><RouterLink to="/play">Zpět ke stolu</RouterLink></PaperSurface>
      </template>

      <template v-else-if="lobby">
        <template v-if="keeper">
          <RouterLink v-if="progress?.keeperState !== 'captured' && lobby.player.xp >= 30 && (wandStatus?.tier ?? 0) < 2" class="aw-button aw-button--primary" to="/play/wand?upgrade=1">Vylepšit hůlku v kufrově dílně</RouterLink>
          <template v-if="progress?.keeperState === 'captured'">
            <p class="game-eyebrow">Ollivander je v zajetí.</p><h1 id="game-title">Dílnu převzal Baron.</h1><GameCharacter class="game-boss-portrait" character="boss" />
            <PaperSurface class="game-paper"><p>„Osobní obsluha? Zrušena. Reklamace? Zamítnuta. Další otázky po mém vítězství.“</p><RouterLink class="aw-button aw-button--primary" to="/play/boss">Vyzvat Barona</RouterLink><p>Dílna se znovu otevře po porážce Barona. Tvoje hůlka i nárok na vylepšení zůstávají zachované.</p></PaperSurface>
          </template>
          <template v-else><p class="game-eyebrow">Digitální dílna</p><h1 id="game-title">Kufr má slovo.</h1><PaperSurface class="game-paper"><h2>{{ progress?.keeperState === 'rescued' ? 'Vždycky jsem ti věřil. Skoro.' : lobby.player.xp >= 30 ? 'Tohle dřevo už ti nestačí.' : 'Každá hůlka začíná od tří kouzel.' }}</h2><p>Odehrál jsi {{ lobby.player.matchesPlayed }} zápasů a získal {{ lobby.player.xp }} XP. {{ lobby.player.tacticalUnlocked ? 'Hůlka II ti otevírá taktiku. Úder, Lest a Štít teď mají omezenou zásobu.' : lobby.player.xp >= 30 ? 'Na vylepšení už máš nárok. Nejdřív ho dokonči v dílně, potom se otevře taktika.' : 'Na 30 XP získáš nárok na vylepšení hůlky. Zatím poznávej soupeře a dej si odvetu.' }}</p><RouterLink class="aw-button aw-button--primary" :to="lobby.player.tacticalUnlocked ? { path: '/play/challenge', query: { mode: 'tactical' } } : '/play/challenge'">{{ lobby.player.tacticalUnlocked ? 'Vyzkoušet taktiku' : 'Vyzvat soupeře' }}</RouterLink></PaperSurface></template>
        </template>
        <template v-else-if="ranking">
          <h1 id="game-title">Žebříček</h1>
          <PaperSurface class="game-paper">
            <AwNotice v-if="progressError" tone="warning">{{ progressError }} {{ progress ? 'Zobrazuje se poslední načtené pořadí.' : '' }}</AwNotice>
            <AwButton v-if="progressError" :busy="loading" @click="refresh">Obnovit pořadí</AwButton>
            <GameRanking v-if="progress" :combat="progress.combatRanking ?? []" :yolo="progress.yoloRanking ?? []" :player-id="account?.id" />
          </PaperSurface>
        </template>
        <template v-else-if="messages">
          <p class="game-eyebrow">Odhaleno tvou hrou</p><h1 id="game-title">Zprávy z dílny</h1><PaperSurface v-for="message in progress?.messages ?? []" :key="message.id" class="game-paper"><h2>{{ message.title }}</h2><p>{{ message.text }}</p><RouterLink :to="message.destination">{{ message.action }} →</RouterLink></PaperSurface>
        </template>
        <template v-else-if="bossSetup">
          <template v-if="bossProfile?.revealed">
            <GameBossStage intro>
              <h1 id="game-title" class="boss-title">Baron <span>Šarlatán</span></h1>
              <p class="boss-captive">{{ bossProfile.rescued ? 'Ollivander je volný.' : 'Ollivander je v zajetí.' }}</p>
            </GameBossStage>
            <section class="boss-invitation" aria-labelledby="boss-invitation-title">
              <h2 id="boss-invitation-title">{{ bossProfile.rescued ? 'Ještě jednu reklamaci?' : 'Je čas zvednout oponu.' }}</h2>
              <p>6 energie proti 5. Nejvýše 12 výměn, tři čitelné fáze. Hůlku ani XP neztrácíš.</p>
              <p v-if="!bossProfile.rescued">První výhra osvobodí Ollivandera všem.</p>
              <RouterLink v-if="bossProfile.activeAttemptId" class="aw-button aw-button--primary" :to="`/play/boss/${bossProfile.activeAttemptId}`">Pokračovat v pokusu</RouterLink><AwButton v-else variant="primary" :busy="busy" :disabled="uncertain" @click="startBoss">Vyzvat Barona</AwButton>
              <details class="boss-terms"><summary>Jak souboj funguje</summary>
                <p>Obtížnost je zmrazená pro celou skupinu. Referenční síla: {{ bossProfile.referenceXp }} XP.</p>
                <p v-if="lobby.player.xp < 30">V exhibici dostaneš dvě použití každého kouzla, žádné vlastní jiskry. Můžeš hrát i bez hůlky.</p>
                <p>Nesoutěžní hra · bez fyzické ceny</p>
              </details>
            </section>
          </template>
          <template v-else><h1 id="game-title">Opona zůstává zavřená.</h1><p>Další kapitola večera zatím nebyla odhalena.</p></template>
        </template>
        <template v-else-if="challenge">
          <p class="game-eyebrow">Místo pro dva</p><h1 id="game-title">Koho vyzveš?</h1>
          <PaperSurface class="game-paper"><form @submit.prevent="start">
            <label class="game-field">Soupeř<select v-model="opponentId" required><option v-for="opponent in lobby.opponents" :key="opponent.id" :value="opponent.id" :disabled="opponent.hasWand === false">{{ opponent.displayName }} · {{ opponent.xp }} XP{{ opponent.hasWand === false ? ' · čeká na hůlku' : '' }}</option></select></label>
            <p v-if="!readyOpponents.length">{{ lobby.opponents.length ? 'Ostatní si zatím připravují hůlky. Mezitím si můžeš dát trénink bez XP.' : 'Zatím není koho vyzvat. Zkus krátký trénink bez XP.' }}</p>
            <fieldset class="game-modes"><legend>Jak se dnes hraje?</legend>
              <label><input v-model="mode" type="radio" value="quick" /> <span><strong>Rychlý duel</strong><small>Nejvýše 7 výměn · končí po rozhodnutí výsledku</small></span></label>
              <label v-if="lobby.player.tacticalUnlocked"><input v-model="mode" type="radio" value="tactical" :disabled="!opponentTacticalReady" /><span><strong>Taktický duel</strong><small>8 výměn · oba potřebují hůlku II a 30 XP</small></span></label>
              <label v-if="lobby.player.yoloUnlocked"><input v-model="mode" type="radio" value="yolo" :disabled="(selectedOpponent?.matchesPlayed ?? 0) < 3" /><span><strong>YOLO lóže</strong><small>AI vymyslí scénář i možnosti podle vašich postav a hůlek. Tři rozhodnutí na každého, bez bojových XP.</small></span></label>
            </fieldset>
            <p v-if="mode === 'yolo'">Příběh použije herní jméno, svět, název hůlky a nejvýše tři herní rysy každé postavy.</p>
            <p>Po prohře se vždy zobrazí úkol. Obsah 18+ a flirt nastavuje pořadatel pro celou skupinu. Výsledek úkolu potvrdíš a uložíš.</p>
            <label v-if="mode === 'tactical' && (progress?.tacticalMatches ?? 0) >= 2" class="game-spark"><input v-model="useLoadouts" type="checkbox" /> Chci vlastní sestavy, pokud je známe oba</label>
            <AwButton type="submit" variant="primary" :busy="busy" :disabled="!canStart || uncertain">Poslat výzvu</AwButton>
          </form></PaperSurface>
        </template>
        <template v-else-if="chronicle">
          <p class="game-eyebrow">Tvé skutečné zápasy</p><h1 id="game-title">Moje kronika</h1>
          <PaperSurface class="game-paper"><p v-if="!lobby.history.length">Zatím nemáš odehraný zápas. Kronikář už si připravil oslavný úvod; jméno vítěze pro jistotu vynechal.</p><ol class="game-match-list"><li v-for="item in lobby.history" :key="item.id"><RouterLink :to="`/play/match/${item.id}`"><span>{{ modeName(item.mode) }}<strong>{{ item.opponent.displayName }}</strong></span><span>{{ item.playerScore }} : {{ item.opponentScore }}<small>+{{ item.xpAwarded }} XP</small></span></RouterLink><p v-if="item.mode === 'tactical' && item.combatNarration?.status === 'complete' && item.combatNarration.text" class="duel-chronicle-narration">{{ item.combatNarration.text }}</p></li></ol></PaperSurface>
        </template>
        <template v-else>
          <h1 id="game-title">Tvůj tah,<br /><em>{{ lobby.player.displayName }}.</em></h1>
          <PaperSurface v-if="!milestoneDismissed && progress?.pending?.length" class="game-paper game-milestone"><p class="game-eyebrow">Nová kapitola</p><h2>{{ progress.pending[0].title }}</h2><p>{{ progress.pending[0].text }}</p><AwButton variant="primary" :busy="busy" @click="acknowledge(progress.pending[0], true)">{{ progress.pending[0].action }}</AwButton><AwButton @click="milestoneDismissed = true">Pokračovat později</AwButton></PaperSurface>
          <section v-if="lobby.active.length || lobby.incoming.length" class="game-open-matches"><h2>U tvého stolu</h2><RouterLink v-for="item in [...lobby.active, ...lobby.incoming]" :key="item.id" class="game-match-link" :to="`/play/match/${item.id}`"><span>{{ item.opponent.displayName }}<small>{{ modeName(item.mode) }}</small></span><span>{{ item.status === 'active' ? 'Pokračovat →' : 'Výzva →' }}</span></RouterLink></section>
          <section v-if="pendingTasks.length" class="game-open-matches"><h2>Úkoly k potvrzení</h2><RouterLink v-for="item in pendingTasks" :key="item.id" class="game-match-link" :to="`/play/match/${item.id}`"><span>{{ item.partyTask!.title }}<small>Po duelu s {{ item.opponent.displayName }}</small></span><span>Potvrdit výsledek →</span></RouterLink></section>
          <div class="game-start">
            <RouterLink class="aw-button aw-button--primary" to="/play/challenge">{{ lobby.opponents.length ? 'Vybrat soupeře' : 'Zatím není koho vyzvat' }}</RouterLink>
            <RouterLink class="game-secondary-link" to="/play/practice">Krátký trénink bez XP</RouterLink>
          </div>
          <nav class="game-mode-links" aria-label="Herní režimy">
            <RouterLink v-if="lobby.player.tacticalUnlocked" :to="{ path: '/play/challenge', query: { mode: 'tactical' } }"><span><strong>Taktický duel</strong><small>Nejvýše osm výměn. Kouzla na příděl, odvaha z vlastních zásob.</small></span><span aria-hidden="true">→</span></RouterLink>
            <RouterLink v-if="(progress?.tacticalMatches ?? 0) >= 3" to="/play/teams"><span><strong>Najít spojence</strong><small>Dva proti dvěma. Každý volí vlastní tah; vina se pak rozděluje společně.</small></span><span aria-hidden="true">→</span></RouterLink>
            <RouterLink v-if="lobby.player.yoloUnlocked" :to="{ path: '/play/challenge', query: { mode: 'yolo' } }"><span><strong>YOLO lóže</strong><small>AI vymyslí scénář i možnosti podle vašich postav a hůlek. Tři rozhodnutí na každého, bez bojových XP.</small></span><span aria-hidden="true">→</span></RouterLink>
            <RouterLink v-if="lobby.boss?.revealed" to="/play/boss"><span><strong>{{ lobby.boss.rescued ? 'Odveta s Baronem' : 'Vyzvat Barona' }}</strong><small>{{ lobby.boss.rescued ? 'Baron čeká na další pokus.' : 'Ollivander je v zajetí. Osvoboď dílnu.' }}</small></span><span aria-hidden="true">→</span></RouterLink>
          </nav>
          <section class="game-wand-summary" aria-label="Tvoje hůlka a postup">
            <img :src="amberWand" alt="" width="1254" height="1254" />
            <div>
              <h2>Hůlka {{ lobby.player.tacticalUnlocked ? 'II' : 'I' }}</h2>
              <p>{{ lobby.player.matchesPlayed }} dokončených zápasů · {{ lobby.player.xp }} XP</p>
              <p>{{ nextMilestone }}</p>
              <div v-if="!lobby.player.tacticalUnlocked" class="game-progress-track" role="progressbar" :aria-valuenow="Math.min(lobby.player.xp, 30)" :aria-valuemax="30" :aria-valuemin="0" aria-label="Postup k hůlce II"><span :style="{ width: `${Math.min(lobby.player.xp / 30, 1) * 100}%` }" /></div>
            </div>
          </section>
          <p v-if="progress?.titles?.length">Tvé tituly: {{ progress.titles.join(' · ') }}</p>
          <nav class="game-side-links" aria-label="Další cesty"><RouterLink to="/play/pod-peceti">Pod pečetí</RouterLink><RouterLink to="/play/chronicle">Moje kronika →</RouterLink><RouterLink to="/play/ranking">Žebříček →</RouterLink><RouterLink v-if="lobby.player.xp >= 30" to="/play/keeper">{{ progress?.keeperState === 'captured' ? 'Dílnu převzal Baron →' : 'Navštívit magický kufr →' }}</RouterLink><RouterLink v-if="progress?.messages?.length" to="/play/messages">Zprávy z dílny →</RouterLink></nav>
        </template>
      </template>
    </div>
  </SceneShell>
</template>
