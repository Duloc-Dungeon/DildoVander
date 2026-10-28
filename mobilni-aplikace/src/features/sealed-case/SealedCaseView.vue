<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import SceneShell from '../../components/scene/SceneShell.vue';
import PaperSurface from '../../components/scene/PaperSurface.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account } from '../identity/session';
import SealedPortrait from './SealedPortrait.vue';
import { errorText, intentNames, intentText, resultText, reasonText, operation, phaseNames, request, SealedError, type Plan, type Play } from './sealedCase';
import '../game/game.css';

const route = useRoute(); const router = useRouter();
const view = ref<Play>(); const loading = ref(false); const busy = ref(false); const error = ref('');
const disconnectId = ref(''); const disconnectConfirmed = ref(false);
const count = ref(8); const variant = ref('first'); const consent = ref(false);
const tab = ref('story'); const draft = ref<Plan>(); const pending = ref<{ operationId: string; plan: Plan }>();
const createKey = () => account.value ? `dv:sealed-create:${account.value.id}` : '';
const oversight = ref<string | null>(null);
const message = ref(''); const recipient = ref('PUBLIC'); const ballot = ref<string[]>([]); const personalText = ref('');
const pane = ref<'own' | 'experienced' | 'presented' | 'planned'>('experienced');
let timer: ReturnType<typeof setTimeout> | undefined; let controller: AbortController | undefined; let generation = 0;
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
function restoreDraft() {
  if (!key()) return;
  try {
    const stored = JSON.parse(localStorage.getItem(key()) ?? 'null');
    if (stored?.draft && Number.isInteger(stored.draft.chapter)) { draft.value = stored.draft; personalText.value = stored.personalText ?? ''; }
    if (stored?.pending?.operationId && stored.pending.plan) pending.value = stored.pending;
  } catch { /* A malformed local draft never becomes a server action. */ }
}
async function refresh() {
  if (loading.value) return;
  loading.value = true; const current = generation;
  controller?.abort(); const requestScope = controller = new AbortController();
  try {
    if (!id.value) {
      const latest = await request<{ id: string | null }>('/current', undefined, requestScope.signal);
      if (latest.id) { await router.replace(`/play/pod-peceti/${encodeURIComponent(latest.id)}`); return; }
      view.value = undefined; return;
    }
    const previousId = view.value?.id;
    const fresh = await request<Play>(`/${encodeURIComponent(id.value)}/play`, undefined, requestScope.signal);
    if (current !== generation) return;
    view.value = fresh; consent.value = fresh.ownScenarioConsent;
    if (previousId !== fresh.id) restoreDraft();
    error.value = '';
  } catch (cause) {
    if (requestScope.signal.aborted || current !== generation) return;
    error.value = errorText(cause);
    if (cause instanceof SealedError && [401, 403, 404].includes(cause.status)) {
      if (key()) localStorage.removeItem(key()); view.value = undefined; draft.value = undefined; pending.value = undefined;
    }
  } finally {
    loading.value = false;
    clearTimeout(timer); timer = setTimeout(refresh, 5000);
  }
}
async function mutate(path: string, body: unknown) {
  if (busy.value) return false;
  busy.value = true; error.value = '';
  try { await request(`/${encodeURIComponent(id.value)}${path}`, body); await refresh(); return true; }
  catch (cause) { error.value = errorText(cause); return false; }
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
function choose(plan: Plan) { pending.value = undefined; draft.value = { ...plan, condition: plan.condition ? { ...plan.condition } : null }; saveDraft(); }
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
watch(() => route.params.id, () => { generation++; controller?.abort(); view.value = undefined; draft.value = undefined; pending.value = undefined; loading.value = false; void refresh(); });
watch(() => account.value?.id, () => { generation++; controller?.abort(); if (key()) localStorage.removeItem(key()); view.value = undefined; draft.value = undefined; pending.value = undefined; });
function resume() { if (document.visibilityState === 'visible') void refresh(); }
onMounted(() => { void refresh(); window.addEventListener('online', resume); document.addEventListener('visibilitychange', resume); });
onBeforeUnmount(() => { generation++; controller?.abort(); clearTimeout(timer); window.removeEventListener('online', resume); document.removeEventListener('visibilitychange', resume); });
</script>

<template>
  <SceneShell class="game-scene" labelled-by="sealed-title"><div class="game-content sealed-content">
    <header class="game-header"><RouterLink to="/play">← Večer</RouterLink><span class="game-eyebrow">Wand Night Stand</span></header>
    <p class="game-eyebrow">Případ posledního zvonu</p><h1 id="sealed-title">Pod <em>pečetí.</em></h1>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }} <AwButton @click="refresh">Obnovit stav</AwButton></AwNotice>
    <p v-if="loading && !view" role="status">Dům hledá správný zápis.</p>
    <PaperSurface v-if="!view && !loading" class="game-paper">
      <h2>Připravit případ</h2><p>Pečeť byla narušena. Dům si pozval svědky. Jejich důvěryhodnost bohužel neprověřoval.</p>
      <label>Varianta<select v-model="variant"><option value="first">První případ · 3 kapitoly · 45 minut</option><option value="full">Plná hra · 6 kapitol</option></select></label>
      <label>Počet hráčů<input v-model.number="count" type="number" min="8" :max="variant === 'first' ? 12 : 30" /></label>
      <p>Každý použije svou uloženou postavu a existující portrét. Místa jsou pro aktivní členy stejného večera.</p>
      <AwButton variant="primary" :busy="busy" :disabled="count < 8 || count > (variant === 'first' ? 12 : 30)" @click="create">Založit případ</AwButton>
    </PaperSurface>
    <template v-if="view">
      <p role="status">{{ phaseNames[view.phase] ?? view.phase }}<template v-if="view.chapter"> · kapitola {{ view.chapter }}/{{ view.chapters }}</template><template v-if="deadline && view.phase !== 'paused'"> · do {{ deadline }}</template></p>
      <PaperSurface v-if="view.phase === 'lobby'" class="game-paper">
        <h2>Místa u stolu</h2><p>{{ view.readyPlayers }} z {{ view.expectedPlayers }} postav je připraveno.</p>
        <label><input v-model="consent" type="checkbox" /> Přijímám scénářovou minulost své postavy, soukromý herní snapshot a závěrečnou rekonstrukci významných příkazů. Celé soukromé rozhovory se automaticky nezveřejní.</label>
        <AwButton v-if="!view.ownReady || !view.ownScenarioConsent" variant="primary" :disabled="!consent" :busy="busy" @click="ready">Připojit mou postavu</AwButton>
        <p v-else>Tvoje postava je připravená. Další výmluvy až při výslechu.</p>
        <AwButton v-if="view.canManage" :disabled="!view.canStart" :busy="busy" @click="mutate('/start', { operationId: operation(), expectedRevision: view.lobbyRevision })">Zahájit případ</AwButton>
        <RouterLink to="/me/character">Moje postava a portrét</RouterLink>
      </PaperSurface>
      <template v-else>
        <PaperSurface class="game-paper"><h2>Tvoje místo v případu</h2><p>{{ !view.ownTeam ? 'Organizátor případu' : view.ownTeam === 'Conspirator' ? 'Spiklenec' : 'Strážce' }} · {{ ({ Badatel: 'Badatel', Strazce: 'Strážce', Stopar: 'Stopař', Prostrednik: 'Prostředník', Iluzionista: 'Iluzionista', Rezonator: 'Rezonátor' } as Record<string, string>)[view.ownKit ?? ''] ?? view.ownKit }} · {{ view.locations[view.location ?? ''] }}</p>
          <p>{{ ({ Awake: 'Bdělý', Sleeping: 'Spící', Unconscious: 'V bezvědomí', Echo: 'Ozvěna' } as Record<string, string>)[view.ownState] ?? view.ownState }}</p>
          <p v-if="view.ownOversight">Pro tuto kapitolu ti byl svěřen dohled.</p><p v-if="view.teamMembers.length">Tvoji Spiklenci: {{ view.teamMembers.map(name).join(', ') }}</p>
        </PaperSurface>
        <nav aria-label="Pohledy na případ" class="sealed-tabs"><AwButton v-for="item in [{ id: 'story', text: 'Příběh' }, { id: 'action', text: 'Moje akce' }, { id: 'journal', text: 'Deník' }, { id: 'messages', text: 'Rozhovory' }]" :key="item.id" :aria-pressed="tab === item.id" @click="tab = item.id">{{ item.text }}</AwButton></nav>
        <PaperSurface v-if="tab === 'story'" class="game-paper"><h2>Dům má připomínky</h2><p>{{ view.publicNarration?.text ?? "Pečeť byla narušena před začátkem tvých tahů. Potvrzené příkazy teprve rozhodnou, co se stane dál." }}</p>
          <p v-if="view.phase === 'quiet'">Hotovo. Telefon teď může stranou. Další kapitola začíná v {{ deadline }}.</p>
          <p v-if="view.phase === 'paused'">Společná pauza. Potvrzené plány zůstávají uložené.</p>
          <p v-if="view.privateNarration || view.notes.length">{{ view.privateNarration?.text ?? view.notes.at(-1) }}</p>
          <AwButton v-if="view.canManage && !['finished', 'paused'].includes(view.phase)" :busy="busy" @click="mutate('/pause', { operationId: operation() })">Společná pauza</AwButton>
          <AwButton v-if="view.canManage && view.phase === 'paused'" :busy="busy" @click="mutate('/resume', { operationId: operation() })">Obnovit případ</AwButton>
        </PaperSurface>
        <PaperSurface v-if="tab === 'action'" class="game-paper"><h2>Moje akce</h2>
          <p v-if="view.ownPlan">Přijatá verze {{ view.ownPlan.revision }}: {{ intentNames[view.ownPlan.intent] }}. Přijetí nepotvrzuje úspěch.</p>
          <p v-else>Žádný potvrzený plán. Při uzávěrce vyčkáváš na svém místě.</p>
          <div v-if="view.phase === 'planning'" class="sealed-choices"><AwButton v-for="choice in view.choices" :key="choice.id" @click="choose(choice.plan)"><strong>{{ choice.label }}</strong><span>{{ choice.benefit }}</span><small>{{ choice.cost }} · {{ choice.risk }}</small></AwButton></div>
          <template v-if="draft">
            <AwNotice v-if="staleDraft">Návrh patří do kapitoly {{ draft.chapter }} / starší revize. Nic se automaticky nepřeneslo.<AwButton @click="rebase">Připravit novou verzi návrhu</AwButton></AwNotice>
            <label><input v-model="draft.shelteredPosition" type="checkbox" /> Zaujmu dosažitelné kryté místo v Archivu nebo Hostinských pokojích. Jinde krytí nevznikne.</label><label>Obranná politika<select v-model="draft.defensePolicy"><option :value="null">Kryj se (výchozí)</option><option :value="0">Kryj se</option><option :value="1">Volej o pomoc</option><option :value="2">Ustup</option></select><small>Použije dostupné možnosti místa. Nepřidává další tah ani ochranný bonus.</small></label>
            <label v-if="view.chapters === 6">Vlastní záměr<textarea v-model="personalText" maxlength="500" placeholder="Popiš úmysl své postavy. Provedení musí odpovídat jednomu tahu a dostupným prostředkům." /></label>
            <label v-if="view.chapters === 6">Proveditelný typ akce<select v-model.number="draft.intent"><option v-for="(label, intent) in intentNames" :key="intent" :value="intent">{{ label }}</option></select></label>
            <label>Adresát / cíl<select v-model="draft.targetId"><option :value="null">Bez cíle / já</option><option v-for="player in view.players.filter(p => p.id !== view!.ownId)" :key="player.id" :value="player.id">{{ player.name }}</option></select></label>
            <label v-if="view.ownState === 'Awake'">Místo<select v-model="draft.destination"><option :value="null">Zůstávám</option><option v-for="place in view.adjacentLocations" :key="place" :value="place">{{ view.locations[place] }}</option></select></label>
            <label v-if="[4, 6, 7, 12, 13, 17].includes(draft.intent)">Předmět / stopa<select v-model="draft.resourceId"><option :value="null">Vyber dostupný zdroj</option><option v-for="item in [...view.ownItems, ...view.visibleItems]" :key="item.id" :value="item.id">{{ item.name }} · {{ name(item.ownerId) }}</option><option v-for="source in view.sources" :key="source.id" :value="source.id">{{ source.label }}</option><option v-for="clue in view.clues.filter(c => !view!.sources.some(s => s.id === c.id))" :key="clue.id" :value="clue.id">{{ clue.id }}</option><option v-if="draft.intent === 13" v-for="(label, place) in view.locations" :key="place" :value="place">{{ label }}</option></select></label>
            <label v-if="draft.intent === 3">Připravit pro<select v-model.number="draft.preparedIntent"><option v-for="(label, intent) in intentNames" :key="intent" :value="intent" :disabled="[0, 1, 3].includes(intent)">{{ label }}</option></select></label>
            <label v-if="draft.intent === 7">Zásah do jediného doručení<select v-model="draft.method"><option value="DELAY">Odložit o jednu kapitolu</option><option value="REDIRECT">Přesměrovat na zvoleného oprávněného adresáta</option></select></label>
            <label v-if="[8, 9].includes(draft.intent)">Prostředek<select v-model="draft.method"><option value="IMPACT_SEAL">Pečeť zásahu</option><option value="BREAKING_SEAL">Zlomová pečeť · jeden Krajní čin</option></select></label>
            <label v-if="draft.intent === 9"><input v-model="draft.lethalConfirmed" type="checkbox" /> Tento čin může usmrtit postavu. Výslovně potvrzuji smrtící úmysl a možnou spotřebu jediného Krajního činu.</label>
            <label v-if="[4, 5, 6, 7, 8, 9, 10, 15, 17].includes(draft.intent)">Podmínka<select v-model="draft.condition"><option :value="null">Bez podmínky</option><option :value="{ kind: 0 }">Až cíl podle mého pozorování vypadá spící</option></select><small>Zdání spánku není důkaz skutečného spánku. Skrytý stav tuto volbu předem nepotvrdí.</small></label>
            <p>Návrh: {{ intentNames[draft.intent] }}. Fallback je vyčkání. Odeslané tvrzení v rozhovoru tento plán nenahradí.</p>
            <AwButton variant="primary" :busy="busy" :disabled="view.phase !== 'planning' || staleDraft || (draft.intent === 9 && !draft.lethalConfirmed)" @click="confirm">Výslovně potvrdit plán</AwButton>
          </template>
          <template v-if="view.phase === 'planning'"><label>Nepovinné doporučení dohledu<select v-model="oversight"><option :value="null">Zdržuji se</option><option v-for="player in view.players.filter(p => p.id !== view!.ownId && !p.state.includes('Ozvěna'))" :key="player.id" :value="player.id">{{ player.name }}</option></select></label><AwButton :busy="busy" @click="mutate('/oversight', { operationId: operation(), chapter: view.chapter, nominee: oversight })">Uložit doporučení</AwButton></template>
          <AwButton v-if="pending" :busy="busy" @click="retryPending">Ověřit původní potvrzení</AwButton>
        </PaperSurface>
        <PaperSurface v-if="tab === 'journal'" class="game-paper"><h2>Můj deník</h2>
          <nav aria-label="Čtyři oddělené kontexty" class="sealed-tabs"><AwButton v-for="item in [{ id: 'experienced', label: 'Zažil jsem' }, { id: 'presented', label: 'Tvrdil jsem' }, { id: 'planned', label: 'Naplánoval jsem' }, { id: 'own', label: 'Doložené stopy' }]" :key="item.id" @click="selectPane(item.id)">{{ item.label }}</AwButton></nav>
          <ul v-if="pane === 'experienced'"><li v-for="(note, index) in view.notes" :key="index">{{ note }}</li></ul>
          <ul v-if="pane === 'presented'"><li v-for="statement in view.statements.filter(s => s.actor === view!.ownId)" :key="statement.id">Tvrzení: {{ statement.body }}</li></ul>
          <ul v-if="pane === 'planned'"><li v-for="(outcome, index) in view.ownOutcomes" :key="index">Kapitola {{ outcome.chapter }} · {{ intentText(outcome.intent) }} · {{ outcome.result }}</li></ul>
          <template v-if="pane === 'own'"><p v-if="!view.clues.length">Zatím nemáš novou doloženou stopu.</p><article v-for="clue in view.clues" :key="clue.id"><h3>{{ clue.id }}</h3><p>Okruh kandidátů: {{ clue.candidates.map(name).join(', ') }}.</p><small>Zdroj: {{ clue.sourceEvent }}</small></article></template>
        </PaperSurface>
        <PaperSurface v-if="tab === 'messages'" class="game-paper"><h2>Rozhovory</h2><p>Tvrzení není důkaz. Soukromý vzkaz dostane jen zvolený adresát.</p>
          <ol><li v-for="statement in view.statements" :key="statement.id"><strong>{{ name(statement.actor) }} · {{ statement.public ? 'veřejně' : 'soukromě' }}</strong><p>{{ statement.body }}</p></li></ol>
          <label>Komu<select v-model="recipient"><option value="PUBLIC">Všem v případu</option><option v-for="player in view.players.filter(p => p.id !== view!.ownId)" :key="player.id" :value="player.id">{{ player.name }}</option></select></label>
          <label>Zpráva<textarea v-model="message" maxlength="1200" /></label><AwButton :busy="busy" :disabled="!message.trim()" @click="sendMessage">Odeslat tvrzení</AwButton>
        </PaperSurface>
        <PaperSurface v-if="view.finale" class="game-paper"><h2>Poslední zvon</h2>
          <template v-if="['ballot', 'runoff'].includes(view.finale.phase)"><p>Vyber {{ view.finale.phase === 'runoff' ? view.finale.runoffPlaces : view.finale.ballotSize }} ostatní hráče, nebo se zdrž hlasu. Každý původní hráč má hlas i jako Ozvěna.</p>
            <label v-for="player in ballotCandidates" :key="player.id"><input v-model="ballot" type="checkbox" :value="player.id" /> {{ player.name }}</label>
            <AwButton :busy="busy" @click="vote()">Potvrdit tajný hlas</AwButton><AwButton :busy="busy" @click="vote(true)">Zdržet se</AwButton><p v-if="view.finale.ownSubmitted">Tvůj hlas je uložený.</p>
          </template>
          <p v-else-if="!view.finale.winner">Hlasování nebo vyhodnocení ještě neskončilo. Dům si zkratku do pravdy nepřipravil.</p>
          <template v-else><article v-if="medallion"><h3>Tvá stopa v případu</h3><p><strong>{{ medallion.name }}</strong> · {{ medallion.team }}</p><p>{{ medallion.text }}</p></article><AwButton @click="exportMemory(false)">Stáhnout veřejnou rekonstrukci</AwButton><AwButton @click="exportMemory(true)">Uložit vlastní vzpomínku</AwButton><p>Vlastní vzpomínka obsahuje tvé zkušenosti a získané stopy. Soukromé rozhovory se do exportu nevkládají.</p><h3>Vítězí {{ view.finale.winner }}.</h3><p>Označení: {{ view.finale.selected?.map(name).join(', ') }}. Spiklenci: {{ view.finale.conspirators?.map(name).join(', ') }}.</p>
            <ol><li v-for="(entry, index) in view.finale.reconstruction" :key="index">{{ entry.chapter }} · {{ name(entry.actor) }} · {{ intentText(entry.intent) }} → {{ resultText(entry.result) }}<span v-if="entry.rolls"> · hod {{ entry.rolls.attack }} proti {{ entry.rolls.defense }}, součty {{ entry.rolls.attackTotal }} : {{ entry.rolls.defenseTotal }}</span><span v-if="entry.reason"> · {{ reasonText(entry.reason) }}</span><small> {{ entry.eventRef }}</small></li></ol>
          </template>
        </PaperSurface>
      </template>
      <PaperSurface v-if="view.canManage && !['lobby', 'finished'].includes(view.phase)" class="game-paper"><h2>Vedení večera</h2><p>Odpojenému hráči zůstává původní místo v počtu hráčů. Jeho další tahy jsou vyčkání a hlas abstence. Při odchodu více než čtvrtiny stolu nejprve společně pozastav případ.</p>
        <label>Odpojená postava<select v-model="disconnectId"><option value="">Vyber postavu</option><option v-for="player in view.players" :key="player.id" :value="player.id">{{ player.name }}</option></select></label>
        <label><input v-model="disconnectConfirmed" type="checkbox" /> Potvrzuji, že tato postava už v partii nepokračuje.</label>
        <AwButton :busy="busy" :disabled="!disconnectId || !disconnectConfirmed" @click="mutate('/disconnect', { operationId: operation(), playerId: disconnectId })">Zapsat odpojení</AwButton></PaperSurface>
      <PaperSurface class="game-paper"><h2>Postavy u stolu</h2><ul class="sealed-roster"><li v-for="player in view.players" :key="player.id"><SealedPortrait v-if="player.portraitUrl" :src="player.portraitUrl" :viewer="view.ownId" :name="player.name" /><span><strong>{{ player.name }}</strong><small>{{ player.seat }} · {{ ({ Awake: 'Bdělý', Sleeping: 'Spící', Unconscious: 'V bezvědomí', Echo: 'Ozvěna' } as Record<string, string>)[player.state] ?? player.state }}<template v-if="player.mask"> · prezentace: {{ player.mask }}</template></small></span></li></ul></PaperSurface>
    </template>
  </div></SceneShell>
</template>

<style scoped>
.sealed-content label { display: grid; gap: .5rem; margin-block: 1rem; line-height: 1.5; }
.sealed-content label:has(input[type=checkbox]) { grid-template-columns: 1.25rem 1fr; align-items: start; }
.sealed-content select, .sealed-content textarea, .sealed-content input[type=number] { width: 100%; min-height: 44px; background: var(--wns-surface-raised); color: var(--wns-text); border: 1px solid var(--wns-line); border-radius: 10px; padding: .75rem; font: inherit; }
.sealed-content textarea { min-height: 100px; resize: vertical; }
.sealed-tabs { display: flex; flex-wrap: wrap; gap: .5rem; margin-block: 1rem; }
.sealed-choices { display: grid; gap: .75rem; }
.sealed-choices button { display: grid; gap: .35rem; text-align: left; padding: 1rem; }
.sealed-roster { list-style: none; padding: 0; display: grid; gap: 1rem; }
.sealed-roster li { display: flex; gap: 1rem; align-items: center; }
.sealed-content small { display: block; color: var(--wns-muted); overflow-wrap: anywhere; }
.sealed-content article { border-top: 1px solid var(--wns-line); padding-block: .75rem; }
</style>
