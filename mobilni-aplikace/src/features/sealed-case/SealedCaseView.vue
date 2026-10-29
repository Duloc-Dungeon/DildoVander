<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import SceneShell from '../../components/scene/SceneShell.vue';
import PaperSurface from '../../components/scene/PaperSurface.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account } from '../identity/session';
import SealedPortrait from './SealedPortrait.vue';
import SealedCaseFinale from './SealedCaseFinale.vue';
import { errorText, intentNames, intentText, resultText, reasonText, operation, phaseNames, request, SealedError, type Plan, type Play } from './sealedCase';
import '../game/game.css';

const route = useRoute(); const router = useRouter();
let mutationFailure: SealedError | undefined;
const view = ref<Play>(); const loading = ref(false); const busy = ref(false); const error = ref('');
const disconnectId = ref(''); const disconnectConfirmed = ref(false);
const count = ref(8); const variant = ref('first'); const consent = ref(false);
const tabs = computed(() => view.value?.ownState === 'Operator'
  ? [{ id: 'story', text: 'Příběh' }, { id: 'messages', text: 'Rozhovory' }]
  : [{ id: 'story', text: 'Příběh' }, { id: 'action', text: 'Moje akce' }, { id: 'journal', text: 'Deník' }, { id: 'messages', text: 'Rozhovory' }]);
const tab = ref('story'); const draft = ref<Plan>(); const pending = ref<{ operationId: string; plan: Plan }>();
const createKey = () => account.value ? `dv:sealed-create:${account.value.id}` : '';
const aiCount = ref(1);
const aiPending = ref<{ operationId: string; expectedRevision: number; count: number }>();
const aiKey = () => account.value && view.value ? `dv:sealed-ai-add:${account.value.id}:${view.value.id}` : '';
const aiRemaining = computed(() => view.value ? view.value.expectedPlayers - view.value.readyPlayers : 0);
const oversight = ref<string | null>(null);
const message = ref(''); const recipient = ref('PUBLIC'); const ballot = ref<string[]>([]); const personalText = ref('');
const pane = ref<'own' | 'experienced' | 'presented' | 'planned'>('experienced');
let timer: ReturnType<typeof setTimeout> | undefined; let controller: AbortController | undefined; let generation = 0;
let disposed = false;
const id = computed(() => typeof route.params.id === 'string' ? route.params.id : '');
const name = (player: string) => view.value?.players.find(p => p.id === player)?.name ?? 'Host';
const key = () => view.value ? `dv:sealed-draft:${view.value.ownId}:${view.value.id}` : '';
const staleDraft = computed(() => !!draft.value && (draft.value.chapter !== view.value?.chapter
  || draft.value.expectedActionRevision !== (view.value?.ownPlan?.revision ?? 0)));
const deadline = computed(() => {
  const v = view.value; if (!v) return '';
  const time = v.phase === 'planning' ? v.planningEndsAt : v.phase === 'quiet' ? v.quietEndsAt : v.finaleEndsAt;
  return time ? new Date(time * 1000).toLocaleString('cs-CZ', { hour: '2-digit', minute: '2-digit', day: 'numeric', month: 'numeric' }) : '';
});
const medallion = computed(() => {
  const v = view.value; if (v?.finale?.phase !== 'released' || !v.ownTeam) return null;
  const confirmedChapters = new Set(v.ownOutcomes.map(entry => entry.chapter));
  const actions = (v.finale.reconstruction ?? []).filter(entry => entry.actor === v.ownId && confirmedChapters.has(entry.chapter));
  const completed = actions.filter(entry => entry.result === 'Success').length;
  return { name: name(v.ownId), team: v.ownTeam === 'Conspirator' ? 'Spiklenec' : 'Strážce', kit: v.ownKit,
    text: `Potvrdil jsi ${actions.length} hlavních plánů. Provedených zásahů: ${completed}. Zbytek doloží rekonstrukce; vlastní verzi už dům od tebe slyšel.`,
    actions, sourceRefs: actions.map(entry => entry.eventRef) };
});
function exportMemory(personal: boolean) {
  const v = view.value; if (!v?.finale || v.finale.phase !== 'released') return;
  const record = { title: 'Pod pečetí · Případ posledního zvonu', caseId: v.id, visibility: personal ? 'Soukromá vzpomínka' : 'Veřejná rekonstrukce',
    winner: v.finale.winner, selected: v.finale.selected, conspirators: v.finale.conspirators,
    reconstruction: v.finale.reconstruction, ...(personal ? { ownId: v.ownId, medallion: medallion.value, ownNotes: v.notes, acquiredFacts: v.clues } : {}) };
  const url = URL.createObjectURL(new Blob([JSON.stringify(record, null, 2)], { type: 'application/json' }));
  const link = document.createElement('a'); link.href = url; link.download = `pod-peceti-${personal ? 'moje-vzpominka' : 'rekonstrukce'}.json`;
  link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
}
function selectPane(value: string) { pane.value = value as 'own' | 'experienced' | 'presented' | 'planned'; }
function saveDraft() {
  if (!key()) return;
  try { localStorage.setItem(key(), JSON.stringify({ draft: draft.value, pending: pending.value, personalText: personalText.value })); } catch { /* Offline draft remains in memory. */ }
}
function restoreAiRequest() {
  try { const stored = JSON.parse(localStorage.getItem(aiKey()) ?? 'null'); if (stored?.operationId) aiPending.value = stored; } catch { /* No automatic submission. */ }
}
async function addAiPlayers(fill = false) {
  const v = view.value; if (!v?.canManageAi || busy.value) return;
  aiPending.value ??= { operationId: operation(), expectedRevision: v.lobbyRevision!, count: fill ? aiRemaining.value : aiCount.value };
  const storageKey = aiKey();
  try { localStorage.setItem(storageKey, JSON.stringify(aiPending.value)); } catch { /* Keep the exact pending operation in memory. */ }
  if (await mutate('/ai-players', aiPending.value)) {
    try { localStorage.removeItem(storageKey); } catch { /* Storage may be unavailable. */ } aiPending.value = undefined;
  } else if (mutationFailure && [400, 403, 404, 409].includes(mutationFailure.status)) {
    // A definitive rejection never accepted this operation; a network failure
    // keeps its exact key for recovery instead.
    const message = error.value;
    try { localStorage.removeItem(storageKey); } catch { /* Storage may be unavailable. */ } aiPending.value = undefined; await refresh(); error.value = message;
  }
}
function restoreDraft() {
  if (!key()) return;
  try {
    const stored = JSON.parse(localStorage.getItem(key()) ?? 'null');
    if (stored?.draft && Number.isInteger(stored.draft.chapter)) { draft.value = stored.draft; personalText.value = stored.personalText ?? ''; }
    if (stored?.pending?.operationId && stored.pending.plan) pending.value = stored.pending;
  } catch { /* A malformed local draft never becomes a server action. */ }
}
async function refresh() {
  if (loading.value || disposed || !account.value) return;
  loading.value = true; const current = generation;
  controller?.abort(); const requestScope = controller = new AbortController();
  try {
    if (!id.value) {
      const latest = await request<{ id: string | null }>('/current', undefined, requestScope.signal);
      if (current !== generation || requestScope.signal.aborted) return;
      if (latest.id) { await router.replace(`/play/pod-peceti/${encodeURIComponent(latest.id)}`); return; }
      view.value = undefined; error.value = ''; return;
    }
    const previousId = view.value?.id;
    const fresh = await request<Play>(`/${encodeURIComponent(id.value)}/play`, undefined, requestScope.signal);
    if (current !== generation) return;
    view.value = fresh; consent.value = fresh.ownScenarioConsent;
    if (previousId !== fresh.id) { restoreDraft(); restoreAiRequest(); }
    error.value = '';
  } catch (cause) {
    if (requestScope.signal.aborted || current !== generation) return;
    error.value = errorText(cause);
    if (cause instanceof SealedError && [401, 403, 404].includes(cause.status)) {
      if (key()) localStorage.removeItem(key()); view.value = undefined; draft.value = undefined; pending.value = undefined;
    }
  } finally {
    if (current === generation && !disposed) {
      loading.value = false;
      clearTimeout(timer); timer = setTimeout(refresh, 5000);
    }
  }
}
async function mutate(path: string, body: unknown) {
  if (busy.value) return false;
  busy.value = true; error.value = ''; mutationFailure = undefined;
  try { await request(`/${encodeURIComponent(id.value)}${path}`, body); await refresh(); return true; }
  catch (cause) { mutationFailure = cause instanceof SealedError ? cause : undefined; error.value = errorText(cause); return false; }
  finally { busy.value = false; }
}
async function create() {
  if (busy.value) return;
  busy.value = true;
  try {
    const storageKey = createKey();
    const saved = storageKey ? localStorage.getItem(storageKey) : null;
    const body = saved ? JSON.parse(saved) : { operationId: operation(), expectedPlayers: count.value, variant: variant.value };
    if (storageKey) localStorage.setItem(storageKey, JSON.stringify(body));
    const created = await request<{ id: string }>('', body);
    if (storageKey) localStorage.removeItem(storageKey);
    await router.push(`/play/pod-peceti/${encodeURIComponent(created.id)}`);
  } catch (cause) { error.value = errorText(cause); }
  finally { busy.value = false; }
}
async function ready() {
  const v = view.value; if (!v || !consent.value) return;
  if (v.ownReady) { await mutate('/scenario-consent', { operationId: operation() }); return; }
  await mutate('/lobby', { operationId: operation(), expectedRevision: v.lobbyRevision, ready: true, consent: true, scenarioConsent: true });
}
function choose(plan: Plan) {
  pending.value = undefined;
  draft.value = {
    ...plan,
    targetId: plan.targetId ?? null,
    destination: plan.destination ?? null,
    resourceId: plan.resourceId ?? null,
    defensePolicy: plan.defensePolicy ?? null,
    condition: plan.condition ? { ...plan.condition } : null,
  };
  saveDraft();
}
function rebase() {
  if (!draft.value || !view.value) return;
  draft.value = { ...draft.value, chapter: view.value.chapter, expectedActionRevision: view.value.ownPlan?.revision ?? 0 };
  pending.value = undefined; saveDraft();
}
async function confirm() {
  if (!draft.value || !view.value || staleDraft.value || view.value.phase !== 'planning') return;
  pending.value ??= { operationId: operation(), plan: { ...draft.value, summary: personalText.value || null, confirmed: true } }; saveDraft();
  if (await mutate('/actions', pending.value)) { pending.value = undefined; draft.value = undefined; personalText.value = ''; saveDraft(); }
}
async function retryPending() {
  if (!pending.value) return;
  if (await mutate('/actions', pending.value)) { pending.value = undefined; draft.value = undefined; saveDraft(); }
}
async function sendMessage() {
  if (!message.value.trim()) return;
  if (await mutate('/statements', { operationId: operation(), body: message.value, public: recipient.value === 'PUBLIC', recipients: recipient.value === 'PUBLIC' ? [] : [recipient.value] })) message.value = '';
}
async function vote(abstain = false) {
  if (!view.value?.finale) return;
  await mutate('/ballots', { operationId: operation(), round: view.value.finale.phase === 'runoff' ? 2 : 1, nominees: abstain ? [] : ballot.value });
}
const ballotCandidates = computed(() => view.value?.players.filter(p => p.id !== view.value!.ownId
  && (view.value!.finale?.phase !== 'runoff' || view.value!.finale.runoffCandidates.includes(p.id))) ?? []);
watch(draft, saveDraft, { deep: true }); watch(personalText, saveDraft);
watch(() => route.params.id, () => { generation++; controller?.abort(); clearTimeout(timer); view.value = undefined; draft.value = undefined; pending.value = undefined; aiPending.value = undefined; loading.value = false; error.value = ''; void refresh(); });
watch(() => account.value?.id, () => { generation++; controller?.abort(); clearTimeout(timer); if (key()) localStorage.removeItem(key()); view.value = undefined; draft.value = undefined; pending.value = undefined; aiPending.value = undefined; loading.value = false; error.value = ''; void refresh(); });
function resume() { if (document.visibilityState === 'visible') void refresh(); }
onMounted(() => { void refresh(); window.addEventListener('online', resume); document.addEventListener('visibilitychange', resume); });
onBeforeUnmount(() => { disposed = true; generation++; controller?.abort(); clearTimeout(timer); window.removeEventListener('online', resume); document.removeEventListener('visibilitychange', resume); });

const journalPanes = [
  { id: 'experienced', label: 'Zažil jsem' },
  { id: 'presented', label: 'Tvrdil jsem' },
  { id: 'planned', label: 'Naplánoval jsem' },
  { id: 'own', label: 'Doložené stopy' },
] as const;
const panel = ref<HTMLElement>();
const isReleased = computed(() => view.value?.finale?.phase === 'released' && !!view.value.finale.winner);
const isBallot = computed(() => !!view.value?.finale && ['ballot', 'runoff'].includes(view.value.finale.phase));
const ballotSize = computed(() => view.value?.finale?.phase === 'runoff'
  ? view.value.finale.runoffPlaces : view.value?.finale?.ballotSize ?? 0);
const roleName = computed(() => !view.value?.ownTeam ? 'Organizátor případu'
  : view.value.ownTeam === 'Conspirator' ? 'Spiklenec' : 'Strážce');
const kitName = computed(() => ({
  Badatel: 'Badatel', Strazce: 'Strážce', Stopar: 'Stopař', Prostrednik: 'Prostředník',
  Iluzionista: 'Iluzionista', Rezonator: 'Rezonátor',
} as Record<string, string>)[view.value?.ownKit ?? ''] ?? view.value?.ownKit);
const stateName = (state: string) => ({
  Awake: 'Bdělý', Sleeping: 'Spící', Unconscious: 'V bezvědomí', Echo: 'Ozvěna',
  Operator: 'Nesoutěžící pozorovatel',
} as Record<string, string>)[state] ?? state;
function clueLabel(id: string) {
  const v = view.value;
  return v?.sources.find(source => source.id === id)?.label
    ?? `Stopa ${(v?.clues.findIndex(clue => clue.id === id) ?? -1) + 1}`;
}
const clueOrigin = (event: string) => event.endsWith(':prelude') ? 'Úvod případu' : 'Získáno během hry';
const ownStatements = computed(() => view.value?.statements.filter(statement => statement.actor === view.value?.ownId) ?? []);
const phaseHint = computed(() => {
  const v = view.value;
  if (!v) return '';
  if (v.phase === 'lobby') return 'Připoj svou postavu. Případ začne, až bude stůl připravený.';
  if (v.phase === 'quiet') return `Hotovo. Telefon teď může stranou.${deadline.value ? ' Další kapitola začíná v ' + deadline.value + '.' : ''}`;
  if (v.phase === 'paused') return 'Společná pauza. Potvrzené plány zůstávají uložené.';
  if (isBallot.value) return 'Poslední rozhodnutí patří stolu. Hlasování je tajné.';
  if (v.phase === 'planning') {
    if (v.ownState === 'Operator') return 'Sleduješ společné dění. Soukromé plány patří hráčům.';
    return v.ownPlan ? 'Tvůj plán je uložený. Do uzávěrky ho můžeš změnit.'
      : 'Vyber a potvrď jeden tah. Bez potvrzení bude tvoje postava vyčkávat.';
  }
  if (v.phase === 'finished' || v.phase === 'finale') return 'Vyhodnocení ještě neskončilo. Rekonstrukce se ukáže po odhalení.';
  return 'Přečti si, co se v domě odehrálo. Tvůj příběh začíná tady.';
});
async function openAction() {
  tab.value = 'action';
  await nextTick();
  panel.value?.focus();
}

</script>


<template>
  <SceneShell class="game-scene" labelled-by="sealed-title">
    <div class="game-content sealed-content">
      <header class="sealed-header">
        <RouterLink to="/play">← Večer</RouterLink>
        <span>Wand Night Stand</span>
      </header>
      <div class="sealed-title">
        <h1 id="sealed-title">Pod <em>pečetí.</em></h1>
        <p>Případ posledního zvonu</p>
      </div>

      <AwNotice v-if="error" tone="danger" role="alert">
        {{ error }} <AwButton @click="refresh">Obnovit stav</AwButton>
      </AwNotice>
      <p v-if="loading && !view" role="status">Dům hledá správný zápis.</p>

      <PaperSurface v-if="!view && !loading" class="sealed-setup">
        <h2>Připravit případ</h2>
        <p>Pečeť byla narušena. Dům si pozval svědky. Jejich důvěryhodnost bohužel neprověřoval.</p>
        <div class="sealed-fields">
          <label>Varianta
            <select v-model="variant">
              <option value="first">První případ · 3 kapitoly · 45 minut</option>
              <option value="full">Plná hra · 6 kapitol</option>
            </select>
          </label>
          <label>Počet hráčů<input v-model.number="count" type="number" min="8" :max="variant === 'first' ? 12 : 30" /></label>
        </div>
        <p class="sealed-muted">Každý použije svou uloženou postavu a existující portrét. Místa jsou pro aktivní členy stejného večera.</p>
        <AwButton variant="primary" :busy="busy" :disabled="!Number.isInteger(count) || count < 8 || count > (variant === 'first' ? 12 : 30)" @click="create">Založit případ</AwButton>
      </PaperSurface>

      <template v-if="view">
        <section v-if="!isReleased" class="sealed-phase" aria-labelledby="sealed-phase-title">
          <div role="status" aria-atomic="true">
            <div class="sealed-phase-line">
              <h2 id="sealed-phase-title">{{ phaseNames[view.phase] ?? 'Případ pokračuje' }}</h2>
              <span v-if="view.chapter && view.phase !== 'lobby'">Kapitola {{ view.chapter }} / {{ view.chapters }}</span>
            </div>
            <p v-if="deadline && !['paused', 'finished', 'lobby'].includes(view.phase)" class="sealed-deadline">Do {{ deadline }}</p>
          </div>
          <p>{{ phaseHint }}</p>
          <AwButton v-if="view.phase === 'planning' && view.ownState !== 'Operator' && tab !== 'action'" variant="primary" @click="openAction">
            {{ view.ownPlan ? 'Prohlédnout můj plán' : draft ? 'Dokončit návrh tahu' : 'Připravit můj tah' }}
          </AwButton>
        </section>

        <PaperSurface v-if="view.phase === 'lobby'" class="sealed-setup">
          <h2>Místa u stolu</h2>
          <p class="sealed-ready"><strong>{{ view.readyPlayers }}</strong> z {{ view.expectedPlayers }} postav je připraveno.</p>
          <template v-if="view.canJoin !== false">
            <template v-if="!view.ownReady || !view.ownScenarioConsent">
              <div class="sealed-consent">
                <h3>Než se připojíš</h3>
                <dl>
                  <div><dt>Co hra použije</dt><dd>Scénářovou minulost tvé postavy a soukromý záznam jejího herního stavu.</dd></div>
                  <div><dt>Co se zveřejní</dt><dd>Závěrečná rekonstrukce významných herních příkazů. Celé soukromé rozhovory se automaticky nezveřejní.</dd></div>
                </dl>
                <label class="sealed-check"><input v-model="consent" type="checkbox" /> Přijímám scénářovou minulost, soukromý herní záznam a závěrečnou rekonstrukci.</label>
              </div>
              <AwButton variant="primary" :disabled="!consent" :busy="busy" @click="ready">Připojit mou postavu</AwButton>
            </template>
            <p v-else class="sealed-confirmed">Tvoje postava je připravená. Další výmluvy až při výslechu.</p>
          </template>
          <p v-else>Řídíš případ jako nesoutěžící pozorovatel. Nemáš přístup k soukromým pohledům hráčů.</p>
          <div v-if="view.canManage" class="sealed-start">
            <AwButton variant="primary" :disabled="!view.canStart" :busy="busy" @click="mutate('/start', { operationId: operation(), expectedRevision: view.lobbyRevision })">Zahájit případ</AwButton>
            <p v-if="!view.canStart" class="sealed-muted">Zahájení se zpřístupní, až budou splněné podmínky případu.</p>
          </div>
          <details v-if="view.canManageAi" class="sealed-disclosure">
            <summary>Doplnit AI hráče</summary>
            <p>Hrají samostatně a zabírají stejná místa jako ostatní. Po uzavření obsazení už hráče nevyměníš.</p>
            <p v-if="!view.aiAvailable" role="status">AI pro tento večer není zapnutá. Nová místa se nepřidají.</p>
            <template v-else-if="aiRemaining > 0">
              <p>Volná místa: {{ aiRemaining }}. AI služba může čerpat rozpočet večera; jeden hráč má nejvýše osm rozhodnutí za partii.</p>
              <label>Počet AI hráčů<input v-model.number="aiCount" type="number" min="1" :max="aiRemaining" step="1" /></label>
              <div class="sealed-buttons">
                <AwButton :busy="busy" :disabled="!Number.isInteger(aiCount) || aiCount < 1 || aiCount > aiRemaining || !!aiPending" @click="addAiPlayers()">Přidat AI hráče</AwButton>
                <AwButton :busy="busy" :disabled="!!aiPending" @click="addAiPlayers(true)">Doplnit volná místa AI</AwButton>
              </div>
            </template>
            <p v-else>Všechna místa jsou obsazená.</p>
            <AwButton v-if="aiPending" :busy="busy" @click="addAiPlayers()">Ověřit původní přidání AI</AwButton>
          </details>
          <RouterLink class="sealed-text-link" to="/me/character">Moje postava a portrét</RouterLink>
        </PaperSurface>

        <template v-else>
          <SealedCaseFinale v-if="isReleased" :view="view" :medallion="medallion" @export="exportMemory" />

          <details class="sealed-identity">
            <summary>
              <span><strong>{{ roleName }}</strong><span class="sealed-identity-place"><template v-if="kitName">{{ kitName }} · </template>{{ view.locations[view.location ?? ''] || 'Místo není uvedené' }}</span></span>
              <span class="sealed-state">{{ stateName(view.ownState) }}</span>
            </summary>
            <p v-if="view.ownOversight">Pro tuto kapitolu ti byl svěřen dohled. Doporučení můžeš uložit v pohledu Moje akce.</p>
            <p v-if="view.teamMembers.length">Tvoji Spiklenci: {{ view.teamMembers.map(name).join(', ') }}</p>
            <p v-if="!view.ownOversight && !view.teamMembers.length">Tady vidíš vlastní roli a stav. Skryté informace ostatních postav zůstávají skryté.</p>
          </details>

          <section v-if="isBallot" class="sealed-ballot" aria-labelledby="sealed-ballot-title">
            <h2 id="sealed-ballot-title">Poslední zvon</h2>
            <template v-if="view.ownState !== 'Operator'">
              <p>Vyber {{ ballotSize }} ostatní hráče, nebo se zdrž hlasu. Každý původní hráč má hlas i jako Ozvěna.</p>
              <fieldset>
                <legend>Tvůj tajný hlas</legend>
                <label v-for="player in ballotCandidates" :key="player.id" class="sealed-check">
                  <input v-model="ballot" type="checkbox" :value="player.id" /> {{ player.name }}
                </label>
              </fieldset>
              <p role="status">Vybráno {{ ballot.length }} z {{ ballotSize }}.</p>
              <div class="sealed-buttons">
                <AwButton variant="primary" :busy="busy" :disabled="ballot.length !== ballotSize" @click="vote()">Potvrdit tajný hlas</AwButton>
                <AwButton :busy="busy" @click="vote(true)">Zdržet se</AwButton>
              </div>
              <p v-if="view.finale?.ownSubmitted" class="sealed-confirmed" role="status">Tvůj hlas je uložený.</p>
            </template>
            <p v-else>Hlasují hráči případu. Jako nesoutěžící pozorovatel vyčkáš na společné odhalení.</p>
          </section>

          <nav aria-label="Pohledy na případ" class="sealed-tabs" :class="{ 'sealed-tabs--two': tabs.length === 2 }">
            <AwButton v-for="item in tabs" :key="item.id" :aria-pressed="tab === item.id" aria-controls="sealed-panel" @click="tab = item.id">{{ item.text }}</AwButton>
          </nav>

          <section id="sealed-panel" ref="panel" tabindex="-1" class="sealed-panel" :aria-label="tabs.find(item => item.id === tab)?.text">
            <template v-if="tab === 'story' && !isReleased">
              <h2>Dům má připomínky</h2>
              <p v-if="view.publicNarration">{{ view.publicNarration.text }}</p>
              <p v-else-if="view.phase === 'finished' || view.phase === 'finale'">Hlasování nebo vyhodnocení ještě neskončilo. Dům si zkratku do pravdy nepřipravil.</p>
              <p v-else>Pečeť byla narušena před začátkem tvých tahů. Potvrzené příkazy teprve rozhodnou, co se stane dál.</p>
              <section v-if="view.privateNarration || view.notes.length" class="sealed-personal">
                <h3>Jen pro tvou postavu</h3>
                <p>{{ view.privateNarration?.text ?? view.notes.at(-1) }}</p>
              </section>
            </template>
            <p v-else-if="tab === 'story'" class="sealed-muted">Případ je uzavřený. Výsledek a rekonstrukci najdeš nahoře, vlastní stopy v deníku.</p>

            <template v-if="tab === 'action' && view.ownState !== 'Operator'">
              <h2>Moje akce</h2>
              <p v-if="view.ownPlan" class="sealed-confirmed">Uložený plán: {{ intentNames[view.ownPlan.intent] }}. Přijetí nepotvrzuje úspěch.</p>
              <p v-else>Bez potvrzeného plánu při uzávěrce vyčkáváš na svém místě. Hra má pro nerozhodnost pevně stanovený postup.</p>
              <p v-if="view.phase !== 'planning'" class="sealed-muted">Teď se nové tahy nepotvrzují. {{ view.phase === 'quiet' ? 'Telefon může stranou; potvrzený plán zůstává uložený.' : 'Další postup určuje aktuální fáze případu.' }}</p>

              <template v-if="view.phase === 'planning'">
                <div class="sealed-choices">
                  <AwButton v-for="choice in view.choices" :key="choice.id" :aria-pressed="draft?.intent === choice.plan.intent" @click="choose(choice.plan)">
                    <strong>{{ choice.label }}</strong><span>{{ choice.benefit }}</span><small>{{ choice.cost }} · {{ choice.risk }}</small>
                  </AwButton>
                </div>
                <p v-if="!view.choices.length">Pro tuto chvíli nejsou dostupné žádné návrhy tahu. Obnov stav nebo vyčkej na další fázi.</p>
                <section v-if="draft" class="sealed-draft" aria-labelledby="sealed-draft-title">
                  <h3 id="sealed-draft-title">Tvůj návrh: {{ intentNames[draft.intent] }}</h3>
                  <p class="sealed-muted">Zatím není odeslaný. Uloží se až tvým výslovným potvrzením.</p>
                  <AwNotice v-if="staleDraft">Kapitola nebo uložený plán se mezitím změnily. Tento návrh se sám nepřenese.
                    <AwButton @click="rebase">Připravit novou verzi návrhu</AwButton>
                  </AwNotice>

                  <label v-if="view.chapters === 6">Vlastní záměr
                    <textarea v-model="personalText" maxlength="500" placeholder="Popiš jeden tah své postavy s dostupnými prostředky." />
                  </label>
                  <label v-if="view.chapters === 6">Proveditelný typ akce
                    <select v-model.number="draft.intent"><option v-for="(label, intent) in intentNames" :key="intent" :value="intent">{{ label }}</option></select>
                  </label>
                  <div class="sealed-fields">
                    <label>Adresát / cíl
                      <select v-model="draft.targetId"><option :value="null">Bez cíle / já</option><option v-for="player in view.players.filter(p => p.id !== view!.ownId)" :key="player.id" :value="player.id">{{ player.name }}</option></select>
                    </label>
                    <label v-if="view.ownState === 'Awake'">Místo
                      <select v-model="draft.destination"><option :value="null">Zůstávám</option><option v-for="place in view.adjacentLocations" :key="place" :value="place">{{ view.locations[place] }}</option></select>
                    </label>
                  </div>
                  <label v-if="[4, 6, 7, 12, 13, 17].includes(draft.intent)">Předmět / stopa
                    <select v-model="draft.resourceId">
                      <option :value="null">Vyber dostupný zdroj</option>
                      <option v-for="item in [...view.ownItems, ...view.visibleItems]" :key="item.id" :value="item.id">{{ item.name }} · {{ name(item.ownerId) }}</option>
                      <option v-for="source in view.sources" :key="source.id" :value="source.id">{{ source.label }}</option>
                      <option v-for="clue in view.clues.filter(c => !view!.sources.some(s => s.id === c.id))" :key="clue.id" :value="clue.id">{{ clueLabel(clue.id) }}</option>
                      <template v-if="draft.intent === 13"><option v-for="(label, place) in view.locations" :key="place" :value="place">{{ label }}</option></template>
                    </select>
                  </label>
                  <label v-if="draft.intent === 3">Připravit pro
                    <select v-model.number="draft.preparedIntent"><option v-for="(label, intent) in intentNames" :key="intent" :value="intent" :disabled="[0, 1, 3].includes(intent)">{{ label }}</option></select>
                  </label>
                  <label v-if="draft.intent === 7">Zásah do jediného doručení
                    <select v-model="draft.method"><option value="DELAY">Odložit o jednu kapitolu</option><option value="REDIRECT">Přesměrovat na zvoleného oprávněného adresáta</option></select>
                  </label>
                  <label v-if="[8, 9].includes(draft.intent)">Prostředek
                    <select v-model="draft.method"><option value="IMPACT_SEAL">Pečeť zásahu</option><option value="BREAKING_SEAL">Zlomová pečeť · jeden Krajní čin</option></select>
                  </label>

                  <details class="sealed-disclosure">
                    <summary>Další možnosti tahu</summary>
                    <label class="sealed-check"><input v-model="draft.shelteredPosition" type="checkbox" /> Zaujmu dosažitelné kryté místo v Archivu nebo Hostinských pokojích. Jinde krytí nevznikne.</label>
                    <label>Jak se bránit
                      <select v-model="draft.defensePolicy"><option :value="null">Kryj se (výchozí)</option><option :value="0">Kryj se</option><option :value="1">Volej o pomoc</option><option :value="2">Ustup</option></select>
                      <small>Použije dostupné možnosti místa. Nepřidává další tah ani ochranný bonus.</small>
                    </label>
                    <label v-if="[4, 5, 6, 7, 8, 9, 10, 15, 17].includes(draft.intent)">Podmínka
                      <select v-model="draft.condition"><option :value="null">Bez podmínky</option><option :value="{ kind: 0 }">Až cíl podle mého pozorování vypadá spící</option></select>
                      <small>Zdání spánku není důkaz skutečného spánku. Skrytý stav tuto volbu předem nepotvrdí.</small>
                    </label>
                  </details>

                  <label v-if="draft.intent === 9" class="sealed-check sealed-lethal"><input v-model="draft.lethalConfirmed" type="checkbox" /> Tento čin může usmrtit postavu. Výslovně potvrzuji smrtící úmysl a možnou spotřebu jediného Krajního činu.</label>
                  <div class="sealed-plan-confirmation">
                    <p><strong>{{ intentNames[draft.intent] }}</strong><template v-if="draft.targetId"> · {{ name(draft.targetId) }}</template><template v-if="draft.destination"> · {{ view.locations[draft.destination] }}</template></p>
                    <p class="sealed-muted">Pokud se záměr neprovede, postava vyčká. Tvrzení v rozhovoru tento plán nenahradí.</p>
                    <AwButton variant="primary" :busy="busy" :disabled="staleDraft || (draft.intent === 9 && !draft.lethalConfirmed)" @click="confirm">Výslovně potvrdit plán</AwButton>
                  </div>
                </section>

                <details v-if="view.ownOversight" class="sealed-disclosure">
                  <summary>Doporučení dohledu</summary>
                  <label>Nepovinné doporučení dohledu
                    <select v-model="oversight"><option :value="null">Zdržuji se</option><option v-for="player in view.players.filter(p => p.id !== view!.ownId && !['Echo', 'Ozvěna'].includes(p.state))" :key="player.id" :value="player.id">{{ player.name }}</option></select>
                  </label>
                  <AwButton :busy="busy" @click="mutate('/oversight', { operationId: operation(), chapter: view.chapter, nominee: oversight })">Uložit doporučení</AwButton>
                </details>
              </template>
              <AwNotice v-if="pending">
                Potvrzení se nepodařilo ověřit. Zkontroluj původní požadavek; nový tah zatím neposílej.
                <AwButton :busy="busy" @click="retryPending">Ověřit původní potvrzení</AwButton>
              </AwNotice>
            </template>

            <template v-if="tab === 'journal' && view.ownState !== 'Operator'">
              <h2>Můj deník</h2>
              <p class="sealed-muted">Co tvoje postava zažila, řekla a skutečně doložila.</p>
              <nav aria-label="Čtyři oddělené kontexty" class="sealed-journal-tabs">
                <AwButton v-for="item in journalPanes" :key="item.id" :aria-pressed="pane === item.id" aria-controls="sealed-journal-pane" @click="selectPane(item.id)">{{ item.label }}</AwButton>
              </nav>
              <div id="sealed-journal-pane">
                <template v-if="pane === 'experienced'">
                  <ul v-if="view.notes.length" class="sealed-entries"><li v-for="(note, index) in view.notes" :key="index">{{ note }}</li></ul>
                  <p v-else class="sealed-empty">Zatím žádná vlastní zkušenost. Nové zápisy přijdou s děním v případu.</p>
                </template>
                <template v-if="pane === 'presented'">
                  <ul v-if="ownStatements.length" class="sealed-entries"><li v-for="statement in ownStatements" :key="statement.id"><small>{{ statement.public ? 'Veřejné tvrzení' : 'Soukromé tvrzení' }}</small>{{ statement.body }}</li></ul>
                  <p v-else class="sealed-empty">Zatím jsi nic netvrdil. Vzkaz můžeš poslat v Rozhovorech; není náhradou za potvrzený tah.</p>
                </template>
                <template v-if="pane === 'planned'">
                  <ul v-if="view.ownOutcomes.length" class="sealed-entries"><li v-for="(outcome, index) in view.ownOutcomes" :key="index"><small>Kapitola {{ outcome.chapter }}</small>{{ intentText(outcome.intent) }} · {{ resultText(outcome.result) }}</li></ul>
                  <p v-else class="sealed-empty">Zatím žádný vyhodnocený tah. Nový plán připravíš v pohledu Moje akce.</p>
                </template>
                <template v-if="pane === 'own'">
                  <p v-if="!view.clues.length" class="sealed-empty">Zatím nemáš novou doloženou stopu. Získat ji můžeš pátráním nebo dalším děním v případu.</p>
                  <article v-for="clue in view.clues" :key="clue.id" class="sealed-clue">
                    <h3>{{ clueLabel(clue.id) }}</h3>
                    <p>Okruh kandidátů: {{ clue.candidates.map(name).join(', ') || 'Žádný známý kandidát' }}.</p>
                    <p class="sealed-muted">Zdroj: {{ clueOrigin(clue.sourceEvent) }}</p>
                  </article>
                </template>
              </div>
            </template>

            <template v-if="tab === 'messages'">
              <h2>Rozhovory</h2>
              <p class="sealed-muted">Tvrzení není důkaz. Soukromý vzkaz dostane jen zvolený adresát.</p>
              <ol v-if="view.statements.length" class="sealed-entries sealed-messages">
                <li v-for="statement in view.statements" :key="statement.id">
                  <p><strong>{{ name(statement.actor) }}</strong> <span class="sealed-message-scope">{{ statement.public ? 'Veřejně' : 'Soukromě' }}</span></p>
                  <p>{{ statement.body }}</p>
                </li>
              </ol>
              <p v-else class="sealed-empty">Zatím tu nikdo nic netvrdil. Podezřelé ticho, ale samo o sobě nic nedokazuje.</p>
              <form v-if="view.ownState !== 'Operator'" class="sealed-message-form" @submit.prevent="sendMessage">
                <label>Komu
                  <select v-model="recipient"><option value="PUBLIC">Všem v případu</option><option v-for="player in view.players.filter(p => p.id !== view!.ownId)" :key="player.id" :value="player.id">{{ player.name }}</option></select>
                </label>
                <p v-if="recipient !== 'PUBLIC'" class="sealed-private-hint">Soukromě pro {{ name(recipient) }}. Tato zpráva není veřejné tvrzení.</p>
                <label>Zpráva<textarea v-model="message" maxlength="1200" placeholder="Napiš, co chce tvoje postava sdělit." /></label>
                <AwButton type="submit" variant="primary" :busy="busy" :disabled="!message.trim()">Odeslat tvrzení</AwButton>
              </form>
            </template>
          </section>
        </template>

        <aside class="sealed-extras" aria-label="Zázemí případu">
          <details class="sealed-disclosure">
            <summary>Postavy u stolu <span class="sealed-summary-count">{{ view.players.length }}</span></summary>
            <ul class="sealed-roster">
              <li v-for="player in view.players" :key="player.id">
                <SealedPortrait v-if="player.portraitUrl" :src="player.portraitUrl" :viewer="view.ownId" :name="player.name" />
                <span><strong>{{ player.name }}<template v-if="player.isAi"> · AI</template></strong><small>{{ player.seat }} · {{ stateName(player.state) }}<template v-if="player.mask"> · prezentace: {{ player.mask }}</template></small></span>
              </li>
            </ul>
          </details>
          <details v-if="view.canManage && !['lobby', 'finished'].includes(view.phase)" class="sealed-disclosure sealed-management">
            <summary>Vedení večera</summary>
            <h3>Průběh případu</h3>
            <AwButton v-if="view.phase !== 'paused'" :busy="busy" @click="mutate('/pause', { operationId: operation() })">Společná pauza</AwButton>
            <AwButton v-else :busy="busy" @click="mutate('/resume', { operationId: operation() })">Obnovit případ</AwButton>
            <h3>Odchod hráče</h3>
            <p>Odpojenému hráči zůstává původní místo v počtu hráčů. Jeho další tahy jsou vyčkání a hlas abstence. Při odchodu více než čtvrtiny stolu nejprve společně pozastav případ.</p>
            <label>Odpojená postava
              <select v-model="disconnectId"><option value="">Vyber postavu</option><option v-for="player in view.players" :key="player.id" :value="player.id">{{ player.name }}</option></select>
            </label>
            <p v-if="disconnectId" class="sealed-muted">Zvolená postava: {{ name(disconnectId) }}</p>
            <label class="sealed-check"><input v-model="disconnectConfirmed" type="checkbox" /> Potvrzuji, že tato postava už v partii nepokračuje.</label>
            <AwButton variant="danger" :busy="busy" :disabled="!disconnectId || !disconnectConfirmed" @click="mutate('/disconnect', { operationId: operation(), playerId: disconnectId })">Zapsat odpojení</AwButton>
          </details>
          <details class="sealed-disclosure">
            <summary>Jak pokračovat ve hře</summary>
            <ul class="sealed-help">
              <li><strong>Moje akce:</strong> vyber návrh, doplň potřebné údaje a výslovně ho potvrď. Samotný výběr ani zpráva v rozhovoru tah neuloží.</li>
              <li><strong>Deník:</strong> odlišuje vlastní zkušenosti, tvrzení, vyhodnocené plány a doložené stopy.</li>
              <li><strong>Klidový úsek:</strong> potvrzený plán se vyhodnocuje. Telefon může stranou.</li>
              <li><strong>Poslední zvon:</strong> hlasuj podle vlastních stop. Po odhalení uvidíš výsledek a můžeš si uložit vzpomínku.</li>
            </ul>
          </details>
        </aside>
      </template>
    </div>
  </SceneShell>
</template>

<style scoped src="./sealedCase.css"></style>
