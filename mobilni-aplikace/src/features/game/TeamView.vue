<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { account } from '../identity/session';
import SceneShell from '../../components/scene/SceneShell.vue';
import PaperSurface from '../../components/scene/PaperSurface.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import GameSpellToken from './GameSpellToken.vue';
import GameRules from './GameRules.vue';
import { actions, actionName, gameRequest, gameErrorText, GameError, operation, type Choice, type Inventory, type Lobby } from './game';
import './game.css';
interface TeamRound {
  index: number; choiceA0: Choice; choiceA1: Choice; choiceB0: Choice; choiceB1: Choice;
  teamAPoints: number; teamBPoints: number;
}
interface TeamMatch {
  id: string; status: string; revision: number; teamMembers: string[]; opponents: string[];
  acceptedBy: string[]; rounds: TeamRound[]; teamScore: number; opponentScore: number;
  completedRounds: number; ownSparks: number; ownInventory: Inventory; ownChoiceLocked: boolean;
  allFourLocked: boolean[]; halftimeEndsAt?: number; result?: string; xpAwarded: number;
  sparkBalances: Record<string, number>; ownTransferLocked: boolean; onTeamA: boolean;
}
const route = useRoute(); const router = useRouter();
const lobby = ref<Lobby>(); const matches = ref<TeamMatch[]>([]); const match = ref<TeamMatch>();
const loading = ref(true); const busy = ref(false); const uncertain = ref(false); const error = ref('');
const partner = ref(''); const opponentOne = ref(''); const opponentTwo = ref('');
const choice = ref<Choice>(); const spark = ref(false);
const halftimeNow = ref(Math.floor(Date.now() / 1000));
const id = computed(() => typeof route.params.id === 'string' ? route.params.id : '');
const eligible = computed(() => lobby.value?.opponents.filter(player => player.xp >= 30) ?? []);
const canCreate = computed(() => (lobby.value?.player.xp ?? 0) >= 30 && [partner.value, opponentOne.value, opponentTwo.value].every(Boolean)
  && new Set([partner.value, opponentOne.value, opponentTwo.value]).size === 3);
const ownId = computed(() => account.value?.id ?? '');
const teammate = computed(() => match.value?.teamMembers.find(user => user !== ownId.value) ?? '');
const name = (user: string) => user === ownId.value ? lobby.value?.player.displayName ?? 'Ty' : lobby.value?.opponents.find(player => player.id === user)?.displayName ?? 'Hráč';
const accepted = computed(() => match.value?.acceptedBy.includes(ownId.value));
const ownPosition = computed(() => Math.max(0, match.value?.teamMembers.indexOf(ownId.value) ?? 0));
const opponent = computed(() => {
  const position = (match.value?.completedRounds ?? 0) < 4 ? ownPosition.value : 1 - ownPosition.value;
  return match.value?.opponents[position];
});
const last = computed(() => match.value?.rounds.at(-1));
const ownLast = computed(() => {
  if (!last.value || !match.value) return '';
  const round = last.value;
  if (match.value.onTeamA) return ownPosition.value === 0
    ? `${actionName(round.choiceA0)} proti ${actionName(round.choiceB0)}`
    : `${actionName(round.choiceA1)} proti ${actionName(round.choiceB1)}`;
  const pair = round.index <= 4 ? ownPosition.value : 1 - ownPosition.value;
  return pair === 0 ? `${actionName(round.choiceB0)} proti ${actionName(round.choiceA0)}` : `${actionName(round.choiceB1)} proti ${actionName(round.choiceA1)}`;
});
const halftimeSecondsRemaining = computed(() => {
  const deadline = match.value?.halftimeEndsAt;
  if (match.value?.status !== 'halftime' || !Number.isFinite(deadline)) return null;
  return Math.max(0, Math.ceil(deadline! - halftimeNow.value));
});
const canTransfer = computed(() => halftimeSecondsRemaining.value !== null && halftimeSecondsRemaining.value > 0);
const controller = new AbortController();
let alive = true; let refreshing = false; let timer: ReturnType<typeof setInterval> | undefined;
let countdownTimer: ReturnType<typeof setInterval> | undefined;
let expiredRefreshKey = '';
let listenersAttached = false;
function update(value: TeamMatch) {
  if (id.value && value.id !== id.value) return;
  if (match.value?.id === value.id && value.revision < match.value.revision) return;
  if (match.value?.completedRounds !== value.completedRounds) { choice.value = undefined; spark.value = false; }
  match.value = value;
}
async function refresh() {
  if (refreshing || busy.value) return;
  refreshing = true;
  try {
    const [players, result] = await Promise.all([
      gameRequest<Lobby>('', undefined, controller.signal),
      id.value ? gameRequest<TeamMatch>(`/teams/${id.value}`, undefined, controller.signal) : gameRequest<{teams: TeamMatch[]}>('/teams', undefined, controller.signal),
    ]);
    if (!alive) return;
    lobby.value = players;
    if (id.value) update(result as TeamMatch); else matches.value = (result as {teams: TeamMatch[]}).teams;
    error.value = ''; uncertain.value = false;
  } catch (cause) {
    if (alive) {
      error.value = gameErrorText(cause);
      if (cause instanceof GameError && [401, 403, 404].includes(cause.status)) { match.value = undefined; matches.value = []; lobby.value = undefined; }
    }
  }
  finally { if (alive) { refreshing = false; loading.value = false; } }
}
async function send(action: string, fields: Record<string, unknown> = {}) {
  if (busy.value || uncertain.value) return;
  if (action === 'transfer' && (match.value?.halftimeEndsAt == null || Date.now() >= match.value.halftimeEndsAt * 1000)) {
    updateHalftimeClock();
    return;
  }
  busy.value = true; error.value = '';
  try {
    const value = await gameRequest<TeamMatch>(action === 'create' ? '/teams' : `/teams/${id.value}/${action}`, {
      operationId: operation(), expectedRevision: match.value?.revision ?? 0, ...fields,
    }, controller.signal);
    if (alive) { if (action === 'create') await router.push(`/play/team/${value.id}`); else update(value); }
  } catch (cause) {
    if (alive) {
      error.value = gameErrorText(cause); uncertain.value = true;
      if (cause instanceof GameError && [401, 403, 404].includes(cause.status)) { match.value = undefined; matches.value = []; lobby.value = undefined; uncertain.value = false; }
    }
  }
  finally { if (alive) { busy.value = false; if (uncertain.value) void refresh(); } }
}
function visible() { if (!document.hidden) void refresh(); }
function updateHalftimeClock() {
  halftimeNow.value = Math.floor(Date.now() / 1000);
  if (halftimeSecondsRemaining.value !== 0 || match.value?.halftimeEndsAt == null) return;
  if (countdownTimer) clearInterval(countdownTimer);
  countdownTimer = undefined;
  const refreshKey = `${match.value.id}:${match.value.halftimeEndsAt}`;
  if (expiredRefreshKey === refreshKey || refreshing || busy.value) return;
  expiredRefreshKey = refreshKey;
  void refresh();
}
function syncHalftimeClock() {
  if (countdownTimer) clearInterval(countdownTimer);
  countdownTimer = undefined;
  expiredRefreshKey = '';
  if (match.value?.status !== 'halftime' || !Number.isFinite(match.value.halftimeEndsAt)) return;
  updateHalftimeClock();
  if (halftimeSecondsRemaining.value !== 0) countdownTimer = setInterval(updateHalftimeClock, 1000);
}
watch([
  () => match.value?.id,
  () => match.value?.status,
  () => match.value?.halftimeEndsAt,
], syncHalftimeClock);
function stopLiveUpdates() {
  if (timer) clearInterval(timer);
  if (countdownTimer) clearInterval(countdownTimer);
  timer = undefined;
  countdownTimer = undefined;
  if (listenersAttached) {
    document.removeEventListener('visibilitychange', visible);
    window.removeEventListener('online', visible);
    listenersAttached = false;
  }
}
watch(() => account.value?.id, () => {
  alive = false; controller.abort(); stopLiveUpdates(); match.value = undefined; matches.value = []; lobby.value = undefined;
}, { flush: 'sync' });
onMounted(() => { void refresh(); timer = setInterval(visible, 3000); document.addEventListener('visibilitychange', visible); window.addEventListener('online', visible); listenersAttached = true; });
onBeforeUnmount(() => { alive = false; controller.abort(); stopLiveUpdates(); });
</script>
<template>
  <SceneShell class="game-scene game-experience" :class="{ 'responsive-table': Boolean(match) }" labelled-by="game-title"><div class="game-content">
    <header class="game-header"><RouterLink :to="id ? '/play/teams' : {path: '/play', query: {browse: '1'}}">← Ke {{ id ? 'spojencům' : 'hře' }}</RouterLink><span class="game-eyebrow">Dva proti dvěma</span><GameRules :team="true" /></header>
    <AwNotice v-if="error" tone="danger">{{ error }} <AwButton @click="refresh">Obnovit stav</AwButton></AwNotice>
    <p v-if="loading" role="status">Připravujeme společný stůl…</p>
    <template v-if="match">
      <div class="game-team-heading"><p class="game-eyebrow">{{ name(teammate) }} je na tvé straně.</p><h1 id="game-title">Čtyři hůlky.<br />Jeden plán.</h1></div>
      <div class="game-score"><div><span>Tvůj tým</span><strong>{{ match.teamScore }}</strong></div><span class="game-score-divider">:</span><div><span>Soupeři</span><strong>{{ match.opponentScore }}</strong></div></div>
      <PaperSurface v-if="match.status === 'incoming'" class="game-paper"><h2>Společná výzva</h2><p>{{ match.teamMembers.map(name).join(' + ') }} proti {{ match.opponents.map(name).join(' + ') }}</p><p>Osm výměn. Po čtyřech vystřídáte soupeře. Nikdo nevypadává a každý potvrzuje vlastní tah.</p><p role="status">Souhlasilo {{ match.acceptedBy.length }} ze 4 hráčů.</p><div v-if="!accepted" class="game-stack"><AwButton variant="primary" :busy="busy" @click="send('accept')">Přijmout výzvu</AwButton><AwButton :busy="busy" @click="send('decline')">Odmítnout</AwButton></div><p v-else>Tvůj souhlas je uložený. Čekáme na ostatní.</p></PaperSurface>
      <PaperSurface v-else-if="match.status === 'halftime'" class="game-paper game-halftime">
        <div class="game-halftime-clock">
          <span>Přestávka</span>
          <span v-if="halftimeSecondsRemaining !== null" role="timer" aria-live="off" :aria-label="`Do konce přestávky zbývá ${halftimeSecondsRemaining} sekund`">{{ halftimeSecondsRemaining }} s</span>
          <span v-else>čas neznámý</span>
        </div>
        <h2>Jiskra pro spojence?</h2>
        <p>Tobě zbývá <strong>{{ match.ownSparks }}</strong>, hráči {{ name(teammate) }} <strong>{{ match.sparkBalances[teammate] ?? 0 }}</strong>.</p>
        <p>Pokud nic nevybereš, nabídneš nulu.</p>
        <details class="game-halftime-details">
          <summary>Jak se předání vyhodnotí</summary>
          <p>Obě nabídky se vyhodnotí současně podle zásob na začátku přestávky. Přijatou jiskru už nelze přeposlat.</p>
        </details>
        <div v-if="!match.ownTransferLocked && canTransfer" class="game-stack">
          <AwButton variant="primary" :busy="busy" :disabled="!match.ownSparks || uncertain" @click="send('transfer', {giveSpark: true})">Předat 1 jiskru</AwButton>
          <AwButton :busy="busy" :disabled="uncertain" @click="send('transfer', {giveSpark: false})">Nic nepředávat</AwButton>
        </div>
        <p v-else-if="match.ownTransferLocked" role="status">Volba uložena. Zásoby potvrdí server po přestávce.</p>
        <p v-else-if="halftimeSecondsRemaining === 0" role="status">Přestávka skončila. Čekáme na potvrzení serveru.</p>
        <p v-else role="status">Čekáme na potvrzení serveru a termín přestávky.</p>
      </PaperSurface>
      <template v-else-if="match.status === 'active'">
        <p class="game-round-label">Výměna {{ match.completedRounds + 1 }} z 8 · proti {{ opponent ? name(opponent) : 'soupeři' }}</p>
        <div class="game-round-dots" aria-hidden="true"><span v-for="round in 8" :key="round" :class="{ done: round <= match.completedRounds, current: round === match.completedRounds + 1 }" /></div>
        <PaperSurface v-if="last" class="game-paper game-reveal" aria-live="polite"><p>{{ ownLast }}. Týmové skóre: {{ match.teamScore }} : {{ match.opponentScore }}.</p></PaperSurface>
        <PaperSurface class="game-paper game-controls"><p v-if="match.ownChoiceLocked" role="status">Tvůj tah je uzamčený. Čekáme na zbývající hráče.</p><template v-else><fieldset class="game-spells"><legend>Vyber své kouzlo</legend><label v-for="action in actions" :key="action.id" :class="{selected: choice === action.id, exhausted: !match.ownInventory[action.id]}"><input v-model="choice" type="radio" name="team-spell" :value="action.id" :disabled="busy || uncertain || !match.ownInventory[action.id]" /><GameSpellToken :action="action.id" /><strong>{{ action.name }}</strong><small>{{ action.beats }}</small><span>{{ match.ownInventory[action.id] }} použití</span></label></fieldset><label class="game-spark"><input v-model="spark" type="checkbox" :disabled="busy || uncertain || !match.ownSparks" /><span>Přisadit jiskru · zbývá {{ match.ownSparks }}<small>Když prohraješ, její bod získají soupeři.</small></span></label><AwButton variant="primary" :busy="busy" :disabled="!choice || uncertain" @click="send('choose', {choice, sparkBid: spark ? 1 : 0})">Potvrdit vlastní tah</AwButton></template></PaperSurface>
      </template>
      <PaperSurface v-else class="game-paper"><h2>{{ match.status === 'complete' ? match.result === 'win' ? 'Tohle byla souhra.' : match.result === 'draw' ? 'Vyrovnaná magie.' : 'Příště to otočíte.' : 'Zápas nebyl dokončen.' }}</h2><p>+{{ match.xpAwarded }} XP</p><RouterLink class="aw-button aw-button--primary" to="/play/teams">Další společná výzva</RouterLink></PaperSurface>
      <AwButton v-if="['incoming','active','halftime'].includes(match.status)" class="game-exit" :busy="busy" :disabled="uncertain" @click="send('cancel')">Ukončit zápas bez výsledku</AwButton>
    </template>
    <template v-else-if="lobby">
      <p class="game-eyebrow">Nikdo nehraje sám.</p><h1 id="game-title">Najdi spojence.</h1>
      <PaperSurface v-if="lobby.player.xp >= 30 && eligible.length >= 3" class="game-paper"><form @submit.prevent="send('create', {partnerId: partner, opponentIds: [opponentOne, opponentTwo]})">
        <label class="game-field">Spoluhráč<select v-model="partner" required><option value="">Vyber hráče</option><option v-for="player in eligible" :key="player.id" :value="player.id">{{ player.displayName }}</option></select></label>
        <label class="game-field">První soupeř<select v-model="opponentOne" required><option value="">Vyber hráče</option><option v-for="player in eligible" :key="player.id" :value="player.id">{{ player.displayName }}</option></select></label>
        <label class="game-field">Druhý soupeř<select v-model="opponentTwo" required><option value="">Vyber hráče</option><option v-for="player in eligible" :key="player.id" :value="player.id">{{ player.displayName }}</option></select></label>
        <p>Každý dostane vlastní výzvu. Zápas začne až po souhlasu všech čtyř.</p><AwButton type="submit" variant="primary" :busy="busy" :disabled="!canCreate || uncertain">Vyzvat čtyřku</AwButton></form></PaperSurface>
      <PaperSurface v-else class="game-paper"><h2>Ještě chvilku.</h2><p>Na týmovou taktiku potřebujeme čtyři hráče s alespoň 30 XP. Rychlá odveta zůstává dostupná.</p><RouterLink to="/play/challenge">Vybrat soupeře →</RouterLink></PaperSurface>
      <section v-if="matches.length"><h2>Vaše společné zápasy</h2><RouterLink v-for="item in matches" :key="item.id" class="game-match-link" :to="`/play/team/${item.id}`"><span>{{ item.teamMembers.map(name).join(' + ') }}<small>proti {{ item.opponents.map(name).join(' + ') }}</small></span><span>{{ item.status === 'incoming' ? 'Výzva →' : item.status === 'complete' ? `${item.teamScore} : ${item.opponentScore}` : 'Pokračovat →' }}</span></RouterLink></section>
    </template>
  </div></SceneShell>
</template>
