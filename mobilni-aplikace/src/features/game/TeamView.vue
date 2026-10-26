<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { account } from '../identity/session';
import SceneShell from '../../components/scene/SceneShell.vue';
import PaperSurface from '../../components/scene/PaperSurface.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import SpellGlyph from '../archive/SpellGlyph.vue';
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
const controller = new AbortController();
let alive = true; let refreshing = false; let timer: ReturnType<typeof setInterval>;
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
watch(() => account.value?.id, () => {
  alive = false; controller.abort(); match.value = undefined; matches.value = []; lobby.value = undefined;
}, { flush: 'sync' });
onMounted(() => { void refresh(); timer = setInterval(visible, 3000); document.addEventListener('visibilitychange', visible); window.addEventListener('online', visible); });
onBeforeUnmount(() => { alive = false; controller.abort(); clearInterval(timer); document.removeEventListener('visibilitychange', visible); window.removeEventListener('online', visible); });
</script>
<template>
  <SceneShell class="game-scene" labelled-by="game-title"><div class="game-content">
    <header class="game-header"><RouterLink :to="id ? '/play/teams' : {path: '/play', query: {browse: '1'}}">← Ke {{ id ? 'spojencům' : 'hře' }}</RouterLink><span class="game-eyebrow">Dva proti dvěma</span></header>
    <AwNotice v-if="error" tone="danger">{{ error }} <AwButton @click="refresh">Obnovit stav</AwButton></AwNotice>
    <p v-if="loading" role="status">Připravujeme společný stůl…</p>
    <template v-if="match">
      <p class="game-eyebrow">{{ name(teammate) }} je na tvé straně.</p><h1 id="game-title">Čtyři hůlky.<br />Jeden plán.</h1>
      <div class="game-score"><div><span>Tvůj tým</span><strong>{{ match.teamScore }}</strong></div><span class="game-score-divider">:</span><div><span>Soupeři</span><strong>{{ match.opponentScore }}</strong></div></div>
      <PaperSurface v-if="match.status === 'incoming'" class="game-paper"><h2>Společná výzva</h2><p>{{ match.teamMembers.map(name).join(' + ') }} proti {{ match.opponents.map(name).join(' + ') }}</p><p>Osm výměn. Po čtyřech vystřídáte soupeře. Nikdo nevypadává a každý potvrzuje vlastní tah.</p><p role="status">Souhlasilo {{ match.acceptedBy.length }} ze 4 hráčů.</p><div v-if="!accepted" class="game-stack"><AwButton variant="primary" :busy="busy" @click="send('accept')">Přijmout výzvu</AwButton><AwButton :busy="busy" @click="send('decline')">Odmítnout</AwButton></div><p v-else>Tvůj souhlas je uložený. Čekáme na ostatní.</p></PaperSurface>
      <PaperSurface v-else-if="match.status === 'halftime'" class="game-paper"><p class="game-eyebrow">Přestávka · nejvýše 15 sekund</p><h2>Jiskra pro spojence?</h2><p>Tobě zbývá {{ match.ownSparks }}, hráči {{ name(teammate) }} zbývá {{ match.sparkBalances[teammate] ?? 0 }}. Předáním se společná zásoba nezvýší.</p><p>Obě nabídky vyřešíme současně ze zásob na začátku přestávky. Přijatou jiskru teď nepřeposíláš.</p><div v-if="!match.ownTransferLocked" class="game-stack"><AwButton variant="primary" :busy="busy" :disabled="!match.ownSparks || uncertain" @click="send('transfer', {giveSpark: true})">Předat 1 jiskru</AwButton><AwButton :busy="busy" :disabled="uncertain" @click="send('transfer', {giveSpark: false})">Nic nepředávat</AwButton></div><p v-else role="status">Volba uložena. Zásoby potvrdí server po přestávce.</p></PaperSurface>
      <template v-else-if="match.status === 'active'">
        <p class="game-round-label">Výměna {{ match.completedRounds + 1 }} z 8 · proti {{ opponent ? name(opponent) : 'soupeři' }}</p>
        <PaperSurface v-if="last" class="game-paper game-reveal" aria-live="polite"><p>{{ ownLast }}. Týmové skóre: {{ match.teamScore }} : {{ match.opponentScore }}.</p></PaperSurface>
        <PaperSurface class="game-paper game-controls"><p v-if="match.ownChoiceLocked" role="status">Tvůj tah je uzamčený. Čekáme na zbývající hráče.</p><template v-else><fieldset class="game-spells"><legend>Vyber své kouzlo</legend><label v-for="action in actions" :key="action.id" :class="{selected: choice === action.id, exhausted: !match.ownInventory[action.id]}"><input v-model="choice" type="radio" name="team-spell" :value="action.id" :disabled="busy || uncertain || !match.ownInventory[action.id]" /><SpellGlyph :action="action.id" /><strong>{{ action.name }}</strong><small>{{ action.beats }}</small><span>{{ match.ownInventory[action.id] }} použití</span></label></fieldset><label class="game-spark"><input v-model="spark" type="checkbox" :disabled="busy || uncertain || !match.ownSparks" /><span>Přisadit jiskru · zbývá {{ match.ownSparks }}<small>Když prohraješ, její bod získají soupeři.</small></span></label><AwButton variant="primary" :busy="busy" :disabled="!choice || uncertain" @click="send('choose', {choice, sparkBid: spark ? 1 : 0})">Potvrdit vlastní tah</AwButton></template></PaperSurface>
      </template>
      <PaperSurface v-else class="game-paper"><h2>{{ match.status === 'complete' ? match.result === 'win' ? 'Tohle byla souhra.' : match.result === 'draw' ? 'Vyrovnaná magie.' : 'Příště to otočíte.' : 'Zápas nebyl dokončen.' }}</h2><p>+{{ match.xpAwarded }} XP</p><RouterLink class="aw-button aw-button--primary" to="/play/teams">Další společná výzva</RouterLink></PaperSurface>
      <AwButton v-if="['incoming','active','halftime'].includes(match.status)" :busy="busy" @click="send('cancel')">Ukončit zápas bez výsledku</AwButton>
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
