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
import SealedEvidenceBoard from './SealedEvidenceBoard.vue';
import SealedExplorationPlan from './SealedExplorationPlan.vue';
import { actionGuidance, clueShare, evidenceChains, planProblem } from './sealedDossier';
import lockIcon from '../../assets/wns-v2/icons/regular/lock-key.svg';
import sealIcon from '../../assets/wns-v2/icons/regular/seal-check.svg';
import { defaultScenarioId, errorText, factionLabel, intentNames, intentText, legacyScenario, memoryRecord,
  resultText, reasonText, operation, phaseNames, request, scenarioFor, SealedError,
  type Clue, type CreateCaseRequest, type Plan, type Play, type ScenarioCatalog } from './sealedCase';
import '../game/game.css';
import './sealedDossier.css';

const route = useRoute(); const router = useRouter();
let mutationFailure: SealedError | undefined;
const view = ref<Play>(); const loading = ref(false); const busy = ref(false); const error = ref('');
const disconnectId = ref(''); const disconnectConfirmed = ref(false);
const count = ref(8); const variant = ref('first'); const consent = ref(false);
const catalog = ref<ScenarioCatalog>(); const scenarioId = ref(defaultScenarioId); const catalogError = ref('');
const pendingCreate = ref<CreateCaseRequest>();
const selectedScenario = computed(() => view.value ? scenarioFor(view.value)
  : catalog.value?.scenarios.find(scenario => scenario.id === scenarioId.value) ?? legacyScenario);
const scenarioAvailable = computed(() => !!catalog.value?.scenarios.some(scenario => scenario.id === scenarioId.value));
const actionNames = computed(() => intentNames.map((label, index) => index === 9 ? selectedScenario.value.extremeActionLabel
  : view.value?.chapters === 3 && index === 2 ? 'Chránit sebe'
    : view.value?.chapters === 3 && index === 7 ? 'Zdržet stopu' : label));
const actionText = (value: string) => value === 'Lethal' ? selectedScenario.value.extremeActionLabel
  : view.value?.chapters === 3 && value === 'Guard' ? 'Chránit sebe'
    : view.value?.chapters === 3 && value === 'Sabotage' ? 'Zdržet stopu' : intentText(value);
const actionChoices = computed(() => {
  const current = view.value;
  if (!current || current.chapters !== 3 || current.ownState !== 'Awake') return current?.choices ?? [];
  const byIntent = (intent: number) => current.choices.find(choice => choice.plan.intent === intent);
  const basis = current.choices[0]?.plan ?? current.ownPlan ?? {
    chapter: current.chapter, expectedActionRevision: 0, confirmed: true, intent: 4,
  };
  const choiceFor = (intent: number, id: string, label: string) => byIntent(intent) ?? {
    id, label, benefit: '', cost: 'Spotřebuje jeden hlavní tah.', risk: '', plan: { ...basis, intent },
  };
  const investigate = choiceFor(4, 'investigate', 'Pátrat');
  const guard = choiceFor(2, 'guard-self', 'Chránit sebe');
  const sabotage = choiceFor(7, 'delay', 'Zdržet stopu');
  return [
    { ...investigate, id: 'short-investigate', label: 'Pátrat', benefit: 'Můžeš získat novou stopu, známou stopu, nebo žádnou novou dostupnou stopu.', cost: 'Spotřebuje jeden hlavní tah.', risk: 'Nový nález není zaručen.' },
    { ...guard, id: 'short-guard-self', label: 'Chránit sebe', benefit: 'Po přesunech blokuje cizí sabotáž ve stejné místnosti v této kapitole.', cost: 'Spotřebuje jeden hlavní tah; nepátráš.', risk: 'Ochrana sama nepotvrzuje, že k útoku došlo.' },
    { ...sabotage, id: 'short-delay', label: 'Zdržet stopu', benefit: 'Odloží nové získání jedné vybrané stopy do další kapitoly; již získané kopie zůstávají.', cost: 'Spotřebuje jeden hlavní tah; nepátráš současně.', risk: 'Může selhat. Zůstaneš u zdroje; přesun plánuj v jiném tahu. V poslední kapitole úspěch blokuje nové pátrací nálezy do hlasování.', plan: { ...sabotage.plan, targetId: null, resourceId: null, destination: null, method: 'DELAY' } },
  ];
});
const tabs = computed(() => view.value?.ownState === 'Operator'
  ? [{ id: 'story', text: 'Příběh' }, { id: 'messages', text: 'Rozhovory' }]
  : [{ id: 'story', text: 'Příběh' }, { id: 'action', text: 'Moje akce' }, { id: 'journal', text: 'Deník' }, { id: 'messages', text: 'Rozhovory' }]);
const tab = ref('story'); const draft = ref<Plan>(); const pending = ref<{ operationId: string; plan: Plan }>();
const lastDraftIntent = ref<number | null>(null);
const createKey = () => account.value ? `dv:sealed-create:${account.value.id}` : '';
const aiCount = ref(1);
const aiPending = ref<{ operationId: string; expectedRevision: number; count: number }>();
const aiKey = () => account.value && view.value ? `dv:sealed-ai-add:${account.value.id}:${view.value.id}` : '';
const aiRemaining = computed(() => view.value ? view.value.expectedPlayers - view.value.readyPlayers : 0);
const oversight = ref<string | null>(null);
const message = ref(''); const recipient = ref('PUBLIC'); const ballot = ref<string[]>([]); const personalText = ref('');
const pane = ref<'own' | 'experienced' | 'presented' | 'planned'>('experienced');
const draftProblem = computed(() => {
  const v = view.value; const plan = draft.value;
  if (v?.chapters === 3 && plan?.intent === 7) {
    if (plan.destination) return 'Zdržení vyžaduje, abys zůstal u zdroje, který teď vidíš. Přesun je samostatný tah.';
    if (!plan.resourceId || !v.sources.some(source => source.id === plan.resourceId)) {
      return 'Vyber jednu z uvolněných stop, které teď vidíš v tomto místě.';
    }
  }
  return planProblem(plan);
});
const chains = computed(() => evidenceChains(view.value?.clues ?? []));
const latestOutcome = computed(() => view.value?.ownOutcomes.at(-1));
const shareStatus = ref('');
let timer: ReturnType<typeof setTimeout> | undefined; let controller: AbortController | undefined; let generation = 0;
let disposed = false;
const id = computed(() => typeof route.params.id === 'string' ? route.params.id : '');
const freshCaseRequested = computed(() => route.query.new === '1');
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
  return { name: name(v.ownId), team: factionLabel(v), kit: v.ownKit,
    text: `Potvrdil jsi ${actions.length} hlavních plánů. Provedených zásahů: ${completed}. Jejich průběh dokládá závěrečná rekonstrukce.`,
    actions, sourceRefs: actions.map(entry => entry.eventRef) };
});
function exportMemory(personal: boolean) {
  const v = view.value; if (!v?.finale || v.finale.phase !== 'released') return;
  const record = memoryRecord(v, personal, medallion.value);
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
    if (stored?.draft && Number.isInteger(stored.draft.chapter)) { draft.value = stored.draft; lastDraftIntent.value = stored.draft.intent; personalText.value = stored.personalText ?? ''; }
    if (stored?.pending?.operationId && stored.pending.plan) pending.value = stored.pending;
  } catch { /* A malformed local draft never becomes a server action. */ }
}
function restoreCreateRequest() {
  pendingCreate.value = undefined;
  try {
    const saved = JSON.parse(localStorage.getItem(createKey()) ?? 'null');
    if (!saved || typeof saved.operationId !== 'string' || !Number.isInteger(saved.expectedPlayers)
      || !['first', 'full'].includes(saved.variant) || (saved.scenarioId !== undefined && typeof saved.scenarioId !== 'string')) return;
    pendingCreate.value = saved; count.value = saved.expectedPlayers; variant.value = saved.variant;
    scenarioId.value = saved.scenarioId ?? defaultScenarioId;
  } catch { /* An invalid local record never becomes a request. */ }
}
async function loadScenarios(signal: AbortSignal, current: number) {
  if (catalog.value) return;
  try {
    const available = await request<ScenarioCatalog>('/scenarios', undefined, signal);
    if (signal.aborted || current !== generation) return;
    if (!Array.isArray(available.scenarios) || !available.scenarios.some(scenario => scenario.id === available.defaultScenarioId)) {
      throw new Error('Invalid public scenario catalog');
    }
    catalog.value = available;
    if (!pendingCreate.value && !available.scenarios.some(scenario => scenario.id === scenarioId.value)) scenarioId.value = available.defaultScenarioId;
    catalogError.value = '';
  } catch (cause) {
    if (signal.aborted || current !== generation) return;
    if (cause instanceof SealedError && cause.status === 404) {
      catalog.value = { defaultScenarioId, scenarios: [legacyScenario] };
      catalogError.value = '';
    } else catalogError.value = 'Nabídku scénářů se nepodařilo načíst. Obnov stav a zkus to znovu.';
  }
}
async function refresh() {
  if (loading.value || disposed || !account.value) return;
  loading.value = true; const current = generation;
  controller?.abort(); const requestScope = controller = new AbortController();
  try {
    if (!id.value) {
      if (!freshCaseRequested.value) {
        const latest = await request<{ id: string | null }>('/current', undefined, requestScope.signal);
        if (current !== generation || requestScope.signal.aborted) return;
        // Discovery identifies the group's latest case, not this account's pending
        // operation. Only the exact creation replay can acknowledge that request.
        if (latest.id && !pendingCreate.value) { await router.replace(`/play/pod-peceti/${encodeURIComponent(latest.id)}`); return; }
      }
      view.value = undefined; error.value = '';
      await loadScenarios(requestScope.signal, current); return;
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
  if (busy.value || !account.value || (!pendingCreate.value && !scenarioAvailable.value)) return;
  const current = generation; const creatorId = account.value.id; const storageKey = createKey();
  busy.value = true; error.value = '';
  try {
    const body = pendingCreate.value ?? { operationId: operation(), expectedPlayers: count.value, variant: variant.value, scenarioId: scenarioId.value };
    pendingCreate.value = body;
    try { if (storageKey) localStorage.setItem(storageKey, JSON.stringify(body)); } catch { /* Keep this exact request in memory. */ }
    const created = await request<{ id: string }>('', body);
    if (current !== generation || account.value?.id !== creatorId || disposed) return;
    try { if (storageKey) localStorage.removeItem(storageKey); } catch { /* The accepted case remains on the server. */ }
    pendingCreate.value = undefined;
    await router.push(`/play/pod-peceti/${encodeURIComponent(created.id)}`);
  } catch (cause) {
    if (current !== generation || account.value?.id !== creatorId || disposed) return;
    error.value = errorText(cause);
    if (cause instanceof SealedError && [400, 403, 404, 409].includes(cause.status)) {
      pendingCreate.value = undefined;
      try { localStorage.removeItem(storageKey); } catch { /* No automatic resubmission. */ }
      if (cause.code === 'sealed_case_scenario_unknown') catalog.value = undefined;
    }
  }
  finally { if (current === generation && account.value?.id === creatorId && !disposed) busy.value = false; }
}
async function ready() {
  const v = view.value; if (!v || !consent.value) return;
  if (v.ownReady) { await mutate('/scenario-consent', { operationId: operation() }); return; }
  await mutate('/lobby', { operationId: operation(), expectedRevision: v.lobbyRevision, ready: true, consent: true, scenarioConsent: true });
}
function sanitizePlan(plan: Plan): Plan {
  const next = { ...plan };
  const intent = next.intent;
  const targetIntents = [2, 5, 6, 7, 8, 9, 10, 12, 17];
  const resourceIntents = [4, 6, 7, 12, 13, 17];
  const shortSelfGuard = intent === 2 && view.value?.chapters === 3;
  const redirectTarget = intent === 7 && view.value?.chapters !== 3 && next.method === 'REDIRECT';
  next.targetId = shortSelfGuard || (intent === 7 && !redirectTarget) || !targetIntents.includes(intent) ? null : next.targetId ?? null;
  next.resourceId = resourceIntents.includes(intent) ? next.resourceId ?? null : null;
  next.preparedIntent = intent === 3 ? next.preparedIntent ?? null : null;
  next.method = intent === 7 || [8, 9].includes(intent) ? next.method ?? null : null;
  if (intent === 7 && !next.method) next.method = 'DELAY';
  next.lethalConfirmed = intent === 9 ? next.lethalConfirmed : false;
  if (![4, 5, 6, 7, 8, 9, 10, 15, 17].includes(intent) || intent === 2 || (view.value?.chapters === 3 && intent === 7)) next.condition = null;
  if (view.value?.chapters === 3 && intent === 7) next.method = 'DELAY';
  if (view.value?.chapters === 3 && intent === 7) next.destination = null;
  if (view.value?.chapters === 3 && [2, 7].includes(intent)) next.shelteredPosition = false;
  return next;
}
function choose(plan: Plan) {
  if (pending.value) return;
  const sameShortTurn = view.value?.chapters === 3 && draft.value?.chapter === plan.chapter
    && draft.value.expectedActionRevision === plan.expectedActionRevision;
  const keepMovement = sameShortTurn && [2, 4].includes(plan.intent) ? draft.value?.destination : null;
  const keepInvestigationSource = sameShortTurn && plan.intent === 4 && draft.value?.intent === 4
    ? draft.value.resourceId : null;
  draft.value = {
    ...plan,
    targetId: plan.targetId ?? null,
    destination: keepMovement ?? plan.destination ?? null,
    resourceId: keepInvestigationSource ?? plan.resourceId ?? null,
    defensePolicy: plan.defensePolicy ?? null,
    condition: plan.condition ? { ...plan.condition } : null,
  };
  draft.value = sanitizePlan(draft.value);
  lastDraftIntent.value = draft.value.intent;
  saveDraft();
}
function changeDraftIntent() {
  if (!draft.value || pending.value) return;
  const changed = draft.value.intent !== lastDraftIntent.value;
  const next = sanitizePlan(draft.value);
  if (changed) {
    // A parameter that belonged to the previous action must be selected again
    // when switching into a target or source action.
    if (next.intent === 2) next.targetId = null;
    if (next.intent === 7) {
      next.targetId = null;
      next.resourceId = null;
      next.method = 'DELAY';
    }
  }
  draft.value = next;
  lastDraftIntent.value = next.intent;
  saveDraft();
}
function changeDraftMethod() {
  if (!draft.value || pending.value) return;
  if (draft.value.intent === 7 && draft.value.method === 'DELAY') draft.value.targetId = null;
  saveDraft();
}
function rebase() {
  if (!draft.value || !view.value) return;
  draft.value = { ...draft.value, chapter: view.value.chapter, expectedActionRevision: view.value.ownPlan?.revision ?? 0 };
  pending.value = undefined; saveDraft();
}
async function confirm() {
  if (!draft.value || !view.value || staleDraft.value || draftProblem.value || view.value.phase !== 'planning') return;
  pending.value ??= { operationId: operation(), plan: { ...draft.value, summary: personalText.value || null, confirmed: true } }; saveDraft();
  if (await mutate('/actions', pending.value)) { pending.value = undefined; draft.value = undefined; personalText.value = ''; saveDraft(); }
}
async function retryPending() {
  if (!pending.value) return;
  if (await mutate('/actions', pending.value)) { pending.value = undefined; draft.value = undefined; saveDraft(); }
}
async function sendMessage() {
  if (!message.value.trim()) return;
  if (await mutate('/statements', { operationId: operation(), body: message.value, public: recipient.value === 'PUBLIC', recipients: recipient.value === 'PUBLIC' ? [] : [recipient.value] })) {
    message.value = ''; shareStatus.value = '';
  }
}
async function prepareShare(clue: Clue) {
  const addition = clueShare(clue, name);
  const combined = message.value.trim() ? `${message.value}\n\n${addition}` : addition;
  tab.value = 'messages';
  if (combined.length > 1200) {
    shareStatus.value = 'Do rozepsané zprávy se stopa nevejde. Nejdřív ji zkrať nebo odešli a pak připrav sdílení znovu.';
  } else {
    message.value = combined;
    shareStatus.value = 'Stopa je připravená ve zprávě. Zkontroluj adresáta a obsah; zatím se nic neodeslalo.';
  }
  await nextTick(); panel.value?.querySelector<HTMLTextAreaElement>('textarea')?.focus();
}
async function prepareTravel(destination: string) {
  const v = view.value;
  if (!v || v.ownState !== 'Awake' || busy.value || pending.value || v.phase !== 'planning' || !v.adjacentLocations.includes(destination)) return;
  if (draft.value?.intent === 0) return;
  const plan = draft.value ?? { chapter: v.chapter, expectedActionRevision: v.ownPlan?.revision ?? 0, confirmed: true, intent: 4 };
  choose({ ...plan, destination });
  await nextTick(); panel.value?.querySelector<HTMLElement>('.sealed-draft')?.scrollIntoView({ block: 'nearest' });
}
async function vote(abstain = false) {
  if (!view.value?.finale) return;
  await mutate('/ballots', { operationId: operation(), round: view.value.finale.phase === 'runoff' ? 2 : 1, nominees: abstain ? [] : ballot.value });
}
const ballotCandidates = computed(() => view.value?.players.filter(p => p.id !== view.value!.ownId
  && (view.value!.finale?.phase !== 'runoff' || view.value!.finale.runoffCandidates.includes(p.id))) ?? []);
watch(draft, saveDraft, { deep: true }); watch(personalText, saveDraft);
watch(() => [route.params.id, account.value?.id], () => { message.value = ''; recipient.value = 'PUBLIC'; shareStatus.value = ''; });
watch(() => [route.params.id, route.query.new], () => { generation++; controller?.abort(); clearTimeout(timer); view.value = undefined; draft.value = undefined; lastDraftIntent.value = null; pending.value = undefined; aiPending.value = undefined; loading.value = false; busy.value = false; error.value = ''; void refresh(); });
watch(() => account.value?.id, () => { generation++; controller?.abort(); clearTimeout(timer); if (key()) localStorage.removeItem(key()); view.value = undefined; draft.value = undefined; lastDraftIntent.value = null; pending.value = undefined; aiPending.value = undefined; catalog.value = undefined; catalogError.value = ''; scenarioId.value = defaultScenarioId; restoreCreateRequest(); loading.value = false; busy.value = false; error.value = ''; void refresh(); });
function resume() { if (document.visibilityState === 'visible') void refresh(); }
onMounted(() => { restoreCreateRequest(); void refresh(); window.addEventListener('online', resume); document.addEventListener('visibilitychange', resume); });
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
  : `Tvoje strana: ${factionLabel(view.value)}`);
const kitName = computed(() => ({
  Badatel: 'Badatel', Strazce: 'Strážce', Stopar: 'Stopař', Prostrednik: 'Prostředník',
  Iluzionista: 'Iluzionista', Rezonator: 'Rezonátor',
} as Record<string, string>)[view.value?.ownKit ?? ''] ?? view.value?.ownKit);
const stateName = (state: string) => ({
  Awake: 'Bdělý', Sleeping: 'Spící', Unconscious: 'V bezvědomí', Echo: selectedScenario.value.echoLabel,
  Operator: 'Nesoutěžící pozorovatel',
} as Record<string, string>)[state] ?? state;
const isEchoState = (state: string) => ['Echo', 'Ozvěna', selectedScenario.value.echoLabel].includes(state);
function clueLabel(id: string) {
  const v = view.value;
  return v?.clues.find(clue => clue.id === id)?.label ?? v?.sources.find(source => source.id === id)?.label
    ?? `Stopa ${(v?.clues.findIndex(clue => clue.id === id) ?? -1) + 1}`;
}
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
  return 'Přečti si úvod případu. Tvůj příběh začíná tady.';
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
        <div>
          <h1 id="sealed-title">Pod <em>pečetí.</em></h1>
          <p>{{ !view && pendingCreate && !scenarioAvailable ? 'Rozpracované založení případu' : selectedScenario.title }}</p>
        </div>
        <span class="sealed-mark" aria-hidden="true"><img :src="lockIcon" alt="" /></span>
      </div>

      <AwNotice v-if="error" tone="danger" role="alert">
        {{ error }} <AwButton @click="refresh">Obnovit stav</AwButton>
      </AwNotice>
      <p v-if="loading && !view" role="status">Načítám případ a dostupné scénáře.</p>

      <PaperSurface v-if="!view && !loading" class="sealed-setup">
        <h2>Připravit případ</h2>
        <AwNotice v-if="catalogError" tone="danger" role="alert">{{ catalogError }} <AwButton @click="refresh">Obnovit nabídku scénářů</AwButton></AwNotice>
        <label for="sealed-scenario">Scénář</label>
        <select id="sealed-scenario" v-model="scenarioId" :aria-describedby="scenarioAvailable || !catalogError ? 'sealed-scenario-summary' : undefined" :disabled="busy || !!pendingCreate || !catalog">
          <option v-for="scenario in catalog?.scenarios ?? []" :key="scenario.id" :value="scenario.id">{{ scenario.title }}</option>
        </select>
        <div v-if="scenarioAvailable" class="sealed-scenario-preview">
          <p class="sealed-muted">{{ selectedScenario.universe }}<template v-if="selectedScenario.tone"> · {{ selectedScenario.tone }}</template></p>
          <p id="sealed-scenario-summary">{{ selectedScenario.summary }}</p>
        </div>
        <p v-else-if="!catalogError" id="sealed-scenario-summary" class="sealed-muted">Vybraný scénář není v aktuální nabídce.</p>
        <AwNotice v-if="pendingCreate">Založení případu zatím není ověřené. Ověření zopakuje původní scénář, délku i počet hráčů a nevytvoří jiný požadavek.</AwNotice>
        <div class="sealed-fields">
          <label>Délka hry
            <select v-model="variant" :disabled="busy || !!pendingCreate">
              <option value="first">První případ · 3 kapitoly · 45 minut</option>
              <option value="full">Plná hra · 6 kapitol</option>
            </select>
          </label>
          <label>Počet hráčů<input v-model.number="count" type="number" min="8" :max="variant === 'first' ? 12 : 30" :disabled="busy || !!pendingCreate" /></label>
        </div>
        <p class="sealed-muted">Každý použije svou uloženou postavu a existující portrét. Místa jsou pro aktivní členy stejného večera.</p>
        <AwButton variant="primary" :busy="busy" :disabled="!pendingCreate && (!scenarioAvailable || !Number.isInteger(count) || count < 8 || count > (variant === 'first' ? 12 : 30))" @click="create">{{ pendingCreate ? 'Ověřit původní založení' : 'Založit případ' }}</AwButton>
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
          <ol v-if="view.chapter > 0 && view.phase !== 'lobby'" class="sealed-chapters" aria-hidden="true">
            <li v-for="chapter in view.chapters" :key="chapter" :class="{ 'is-past': chapter < view.chapter, 'is-current': chapter === view.chapter }">
              <span>{{ chapter }}</span>
            </li>
          </ol>
          <p>{{ phaseHint }}</p>
          <AwButton v-if="view.phase === 'planning' && view.ownState !== 'Operator' && tab !== 'action'" variant="primary" @click="openAction">
            {{ view.ownPlan ? 'Prohlédnout můj plán' : draft ? 'Dokončit návrh tahu' : 'Připravit můj tah' }}
          </AwButton>
        </section>

        <PaperSurface v-if="view.phase === 'lobby'" class="sealed-setup">
          <p class="sealed-scenario-premise">{{ selectedScenario.summary }}</p>
          <h2>Místa u stolu</h2>
          <p class="sealed-ready"><strong>{{ view.readyPlayers }}</strong> z {{ view.expectedPlayers }} postav je připraveno.</p>
          <template v-if="view.canJoin !== false">
            <template v-if="!view.ownReady || !view.ownScenarioConsent">
              <div class="sealed-consent">
                <h3>Než se připojíš</h3>
                <details class="sealed-disclosure sealed-rules">
                  <summary>Krátká pravidla případu</summary>
                  <ul class="sealed-help">
                    <li>Máš vlastní soukromý cíl. Na konci tajně hlasuješ o podezřelých; hlasuj podle toho, čemu sám věříš.</li>
                    <li>V jednom tahu zvolíš jednu hlavní akci a můžeš se přesunout do sousedního místa. Návrh začne platit až po výslovném potvrzení.</li>
                    <li>Vlastní stopa je doložený důkaz. Zpráva druhého je jeho tvrzení; tvůj odhad zůstává podezřením. Úplný vlastní řetězec A/B/C umožní správný závěr.</li>
                    <li>Stopy A, B a C se uvolňují na různých místech a v různých kapitolách. Cesta do posledního místa se plánuje před poslední kapitolou.</li>
                    <li>Chránit sebe stojí hlavní tah; přítomná ochrana v této kapitole blokuje cizí sabotáž ve stejné místnosti, sama ale nedokazuje útok.</li>
                    <li>Zdržení stopy ovlivní její nové získání pátráním do další kapitoly. Již získané kopie zůstávají; úspěšné zdržení v poslední kapitole platí až do hlasování.</li>
                    <li>Soukromý telefon, role, spis a potvrzený plán patří hráči; neukazuj je na cizím telefonu ani na snímku obrazovky. Informace sdílej ústně nebo vědomě odeslanou zprávou.</li>
                  </ul>
                </details>
                <section v-if="view.publicNarration?.text" class="sealed-prelude" aria-labelledby="sealed-prelude-title">
                  <h4 id="sealed-prelude-title">Co se stalo před hrou</h4>
                  <p>{{ view.publicNarration.text }}</p>
                </section>
                <dl>
                  <div><dt>Co hra použije</dt><dd>Scénářovou minulost tvé postavy a soukromý záznam jejího herního stavu.</dd></div>
                  <div><dt>Co se zveřejní</dt><dd>Závěrečná rekonstrukce významných herních příkazů. Celé soukromé rozhovory se automaticky nezveřejní.</dd></div>
                  <div><dt>{{ selectedScenario.echoLabel }}</dt><dd>{{ selectedScenario.echoExplanation }}</dd></div>
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
          <SealedCaseFinale v-if="isReleased" :view="view" :medallion="medallion" @export="exportMemory" @new-case="router.push('/play/pod-peceti?new=1')" />

          <details class="sealed-identity">
            <summary>
              <span><strong>{{ roleName }}</strong><span class="sealed-identity-place"><template v-if="kitName">{{ kitName }} · </template>{{ view.locations[view.location ?? ''] || 'Místo není uvedené' }}</span></span>
              <span class="sealed-state">{{ stateName(view.ownState) }}</span>
            </summary>
            <p v-if="view.ownOversight">Pro tuto kapitolu ti byl svěřen dohled. Doporučení můžeš uložit v pohledu Moje akce.</p>
            <p v-if="view.ownObjective">{{ view.ownObjective }}</p>
            <p v-if="view.ownState === 'Echo'">{{ selectedScenario.echoExplanation }}</p>
            <p v-if="view.teamMembers.length">Tvoje strana · {{ selectedScenario.conspiratorLabel }}: {{ view.teamMembers.map(name).join(', ') }}</p>
            <p v-if="!view.ownOversight && !view.teamMembers.length">Tady vidíš vlastní roli a stav. Skryté informace ostatních postav zůstávají skryté.</p>
          </details>

          <section v-if="isBallot" class="sealed-ballot" aria-labelledby="sealed-ballot-title">
            <h2 id="sealed-ballot-title">Závěrečné hlasování</h2>
            <template v-if="view.ownState !== 'Operator'">
              <p>Vyber {{ ballotSize }} ostatní hráče, nebo se zdrž hlasu. Každý původní hráč má hlas i ve stavu {{ selectedScenario.echoLabel }}.</p>
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
              <h2>{{ view.chapterTitle || 'Příběh případu' }}</h2>
              <p v-if="view.publicNarration">{{ view.publicNarration.text }}</p>
              <p v-else-if="view.phase === 'finished' || view.phase === 'finale'">Hlasování nebo vyhodnocení ještě neskončilo. Rekonstrukce se ukáže po odhalení.</p>
              <p v-else>{{ selectedScenario.summary }}</p>
              <section v-if="view.privateNarration || view.notes.length" class="sealed-personal">
                <h3>Jen pro tvou postavu</h3>
                <p>{{ view.privateNarration?.text ?? view.notes.at(-1) }}</p>
              </section>
              <section v-if="view.ownState !== 'Operator'" class="sealed-dossier-door" aria-labelledby="sealed-dossier-door-title">
                <h3 id="sealed-dossier-door-title">Vyšetřovací spis</h3>
                <p v-if="chains.length">Vlastní stopy: {{ view.clues.length }} · úplné řetězce: {{ chains.filter(chain => !chain.missing.length).length }} z {{ chains.length }}.</p>
                <p v-else>Každá stopa má své místo. Zatím na tebe všechna čekají.</p>
                <AwButton @click="tab = 'journal'; pane = 'own'">Otevřít důkazní řetězce</AwButton>
              </section>
              <section v-if="latestOutcome && view.ownState !== 'Operator'" class="sealed-latest-outcome" aria-labelledby="sealed-latest-title">
                <h3 id="sealed-latest-title">Poslední vyhodnocený tah</h3>
                <p>Kapitola {{ latestOutcome.chapter }} · {{ actionText(latestOutcome.intent) }}</p>
                <p>{{ resultText(latestOutcome.result) }}</p>
                <p v-if="latestOutcome.observation">{{ latestOutcome.observation }}</p>
                <template v-if="latestOutcome.intent === 'Sabotage' && latestOutcome.actionObservation?.chapter === latestOutcome.chapter">
                  <p v-if="latestOutcome.actionObservation.kind === 'DELAY_SUCCEEDED'">Tvá zaznamenaná zkušenost potvrzuje úspěšné zdržení nové dostupnosti této stopy.</p>
                  <p v-else-if="latestOutcome.actionObservation.kind === 'DELAY_NOT_OBSERVED'">Nemáš oprávněný podklad k potvrzení úspěchu. Výsledek zůstává neznámý; neříká nic o cizí ochraně.</p>
                </template>
                <p v-else-if="latestOutcome.intent === 'Sabotage'">Výsledek zdržení zatím z tvé zkušenosti neznáš.</p>
              </section>
              <SealedExplorationPlan v-if="view.ownState === 'Awake' && ['planning', 'quiet'].includes(view.phase)" :play="view" />
            </template>
            <p v-else-if="tab === 'story'" class="sealed-muted">Případ je uzavřený. Výsledek a rekonstrukci najdeš nahoře, vlastní stopy v deníku.</p>

            <template v-if="tab === 'action' && view.ownState !== 'Operator'">
              <h2>Moje akce</h2>
              <div v-if="view.ownPlan" :key="`${view.id}:${view.ownPlan.chapter}:${view.ownPlan.revision}`" class="sealed-receipt" role="status">
                <span class="sealed-receipt-stamp" aria-hidden="true"><img :src="sealIcon" alt="" /></span>
                <div>
                  <strong>Uložený plán: {{ actionNames[view.ownPlan.intent] }}</strong>
                  <p>Tvůj plán je uložený a čeká na společné vyhodnocení.</p>
                  <small>Přijetí nepotvrzuje úspěch.</small>
                </div>
              </div>
              <p v-else>Bez potvrzeného plánu při uzávěrce vyčkáváš na svém místě. Hra má pro nerozhodnost pevně stanovený postup.</p>
              <p v-if="view.phase !== 'planning'" class="sealed-muted">Teď se nové tahy nepotvrzují. {{ view.phase === 'quiet' ? 'Telefon může stranou; potvrzený plán zůstává uložený.' : 'Další postup určuje aktuální fáze případu.' }}</p>

              <template v-if="view.phase === 'planning'">
                <SealedExplorationPlan v-if="view.ownState === 'Awake'" :play="view" :actionable="!pending && !busy && draft?.intent !== 0 && !(view.chapters === 3 && draft?.intent === 7)" @travel="prepareTravel" />
                <div class="sealed-choices">
                  <AwButton v-for="choice in actionChoices" :key="choice.id" :aria-pressed="draft?.intent === choice.plan.intent" :disabled="!!pending || (view.chapters === 3 && choice.plan.intent === 7 && !view.sources.length)" @click="choose(choice.plan)">
                    <strong>{{ choice.label }}</strong><span>{{ choice.benefit }}</span><small>{{ choice.cost }} · {{ choice.risk }}</small>
                  </AwButton>
                </div>
                <p v-if="view.chapters === 3 && view.ownState === 'Awake' && !view.sources.length" class="sealed-muted">Zdržení zatím není možné: v tomto místě nevidíš žádnou uvolněnou stopu k výběru. Veřejný rozpis neodhaluje budoucí obsah.</p>
                <p v-else-if="!actionChoices.length">Pro tuto chvíli nejsou dostupné žádné návrhy tahu. Obnov stav nebo vyčkej na další fázi.</p>
                <section v-if="draft" class="sealed-draft" aria-labelledby="sealed-draft-title">
                  <h3 id="sealed-draft-title">Tvůj návrh: {{ actionNames[draft.intent] }}</h3>
                  <p class="sealed-muted">Zatím není odeslaný. Uloží se až tvým výslovným potvrzením.</p>
                  <AwNotice v-if="staleDraft">Kapitola nebo uložený plán se mezitím změnily. Tento návrh se sám nepřenese.
                    <AwButton @click="rebase">Připravit novou verzi návrhu</AwButton>
                  </AwNotice>

                  <label v-if="view.chapters === 6">Vlastní záměr
                    <textarea v-model="personalText" maxlength="500" placeholder="Popiš jeden tah své postavy s dostupnými prostředky." />
                  </label>
                  <label v-if="view.chapters === 6">Proveditelný typ akce
                    <select v-model.number="draft.intent" :disabled="!!pending" @change="changeDraftIntent"><option v-for="(label, intent) in actionNames" :key="intent" :value="intent">{{ label }}</option></select>
                  </label>
                  <p v-if="actionGuidance(draft.intent, draft.method ?? undefined, draft.targetId ?? undefined)" class="sealed-action-guidance">{{ actionGuidance(draft.intent, draft.method ?? undefined, draft.targetId ?? undefined) }}</p>
                  <div class="sealed-fields">
                    <label v-if="(draft.intent !== 2 || view.chapters !== 3) && (draft.intent !== 7 || (view.chapters !== 3 && draft.method === 'REDIRECT'))">Adresát / cíl
                      <select v-model="draft.targetId"><option :value="null">Bez cíle / já</option><option v-for="player in view.players.filter(p => p.id !== view!.ownId)" :key="player.id" :value="player.id">{{ player.name }}</option></select>
                    </label>
                    <label v-if="view.ownState === 'Awake' && !(view.chapters === 3 && draft.intent === 7)">Místo
                      <select v-model="draft.destination"><option :value="null">Zůstávám</option><option v-for="place in view.adjacentLocations" :key="place" :value="place">{{ view.locations[place] }}</option></select>
                    </label>
                  </div>
                  <label v-if="[4, 6, 7, 12, 13, 17].includes(draft.intent)">{{ draft.intent === 4 ? 'Viditelná stopa k pátrání (volitelné)' : 'Předmět / stopa' }}
                    <select v-model="draft.resourceId">
                      <option :value="null">{{ draft.intent === 4 ? 'Bez určení zdroje' : 'Vyber dostupný zdroj' }}</option>
                      <template v-if="draft.intent === 4 || (draft.intent === 7 && view.chapters === 3)">
                        <option v-for="source in view.sources" :key="source.id" :value="source.id">{{ source.label }}</option>
                      </template>
                      <template v-else>
                        <option v-for="item in [...view.ownItems, ...view.visibleItems]" :key="item.id" :value="item.id">{{ item.name }} · {{ name(item.ownerId) }}</option>
                        <option v-for="source in view.sources" :key="source.id" :value="source.id">{{ source.label }}</option>
                        <option v-for="clue in view.clues.filter(c => !view!.sources.some(s => s.id === c.id))" :key="clue.id" :value="clue.id">{{ clueLabel(clue.id) }}</option>
                      </template>
                      <template v-if="draft.intent === 13"><option v-for="(label, place) in view.locations" :key="place" :value="place">{{ label }}</option></template>
                    </select>
                    <small v-if="draft.intent === 7 && view.chapters === 3">ID zdroje samo o sobě nezpřístupňuje jeho obsah ani nevytváří vlastní stopu.</small>
                  </label>
                  <label v-if="draft.intent === 3">Připravit pro
                    <select v-model.number="draft.preparedIntent"><option :value="null">Vyber účinnou akci</option><option v-for="intent in [6, 8, 9]" :key="intent" :value="intent">{{ actionNames[intent] }}</option></select>
                  </label>
                  <p v-if="draft.intent === 7 && view.chapters === 3" class="sealed-muted">Zdržení ovlivňuje novou dostupnost celé stopy pro pátrání, ne jen jedno doručení jednomu člověku. Další kapitola stopu znovu zpřístupní; poslední kapitola přejde rovnou k hlasování.</p>
                  <p v-if="draft.intent === 7 && view.chapters === 3" class="sealed-muted">Zůstáváš v místě zdroje, který teď vidíš. Přesun do jiného místa vyžaduje samostatný tah.</p>
                  <label v-if="draft.intent === 7 && view.chapters !== 3">Metoda sabotáže
                    <select v-model="draft.method" @change="changeDraftMethod"><option value="DELAY">Zdržet celou stopu o jednu kapitolu</option><option value="REDIRECT">Přesměrovat jedno doručení k oprávněnému adresátovi</option></select>
                  </label>
                  <p v-if="draft.intent === 7 && view.chapters === 6 && draft.method === 'DELAY'" class="sealed-muted">Zdržení mění dostupnost celé konkrétní stopy pro nové pátrání; již získané kopie zůstávají.</p>
                  <p v-else-if="draft.intent === 7 && view.chapters === 6 && draft.method === 'REDIRECT'" class="sealed-muted">Přesměrování mění adresáta jednoho doručení. Jeho obsah ani historie stopy se tím nemění.</p>
                  <label v-if="[8, 9].includes(draft.intent)">Prostředek
                    <select v-model="draft.method"><option value="IMPACT_SEAL">Pečeť zásahu</option><option value="BREAKING_SEAL">Zlomová pečeť · jeden Krajní čin</option></select>
                  </label>

                  <details v-if="view.chapters === 6" class="sealed-disclosure">
                    <summary>Další možnosti tahu</summary>
                    <label class="sealed-check"><input v-model="draft.shelteredPosition" type="checkbox" /> Zaujmu dosažitelné kryté místo: {{ view.locations.ARCHIV ?? 'Archiv' }} nebo {{ view.locations.POKOJE ?? 'Hostinské pokoje' }}. Jinde krytí nevznikne.</label>
                    <label>Jak se bránit
                      <select v-model="draft.defensePolicy"><option :value="null">Kryj se (výchozí)</option><option :value="0">Kryj se</option><option :value="1">Volej o pomoc</option><option :value="2">Ustup</option></select>
                      <small>Použije dostupné možnosti místa. Nepřidává další tah ani ochranný bonus.</small>
                    </label>
                    <label v-if="[4, 5, 6, 7, 8, 9, 10, 15, 17].includes(draft.intent)">Podmínka
                      <select v-model="draft.condition"><option :value="null">Bez podmínky</option><option :value="{ kind: 0 }">Až cíl podle mého pozorování vypadá spící</option></select>
                      <small>Zdání spánku není důkaz skutečného spánku. Skrytý stav tuto volbu předem nepotvrdí.</small>
                    </label>
                  </details>

                  <label v-if="draft.intent === 9" class="sealed-check sealed-lethal"><input v-model="draft.lethalConfirmed" type="checkbox" /><span><strong>Výslovně potvrzuji tento čin.</strong> {{ selectedScenario.extremeConsequence }}</span></label>
                  <div class="sealed-plan-confirmation">
                    <p><strong>{{ actionNames[draft.intent] }}</strong><template v-if="draft.targetId"> · {{ name(draft.targetId) }}</template><template v-if="draft.destination"> · {{ view.locations[draft.destination] }}</template></p>
                    <p class="sealed-muted">Pokud se záměr neprovede, postava vyčká. Tvrzení v rozhovoru tento plán nenahradí.</p>
                    <p v-if="draftProblem" role="status">{{ draftProblem }}</p>
                    <AwButton variant="primary" :busy="busy" :disabled="staleDraft || !!draftProblem || (draft.intent === 9 && !draft.lethalConfirmed)" @click="confirm">Výslovně potvrdit plán</AwButton>
                  </div>
                </section>

                <details v-if="view.ownOversight" class="sealed-disclosure">
                  <summary>Doporučení dohledu</summary>
                  <label>Nepovinné doporučení dohledu
                    <select v-model="oversight"><option :value="null">Zdržuji se</option><option v-for="player in view.players.filter(p => p.id !== view!.ownId && !isEchoState(p.state))" :key="player.id" :value="player.id">{{ player.name }}</option></select>
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
              <h2>Vyšetřovací spis</h2>
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
                  <ul v-if="view.ownOutcomes.length" class="sealed-entries"><li v-for="(outcome, index) in view.ownOutcomes" :key="index"><small>Kapitola {{ outcome.chapter }}</small>{{ actionText(outcome.intent) }} · {{ resultText(outcome.result) }}<p v-if="outcome.observation">{{ outcome.observation }}</p></li></ul>
                  <p v-else class="sealed-empty">Zatím žádný vyhodnocený tah. Nový plán připravíš v pohledu Moje akce.</p>
                </template>
                <template v-if="pane === 'own'">
                  <SealedEvidenceBoard :play="view" @share="prepareShare" />
                </template>
              </div>
            </template>

            <template v-if="tab === 'messages'">
              <h2>Rozhovory</h2>
              <p class="sealed-muted">Tvrzení není důkaz. Soukromý vzkaz dostane jen zvolený adresát.</p>
              <p v-if="view.ownState !== 'Operator'" class="sealed-message-count">Tvoje odeslaná sdělení: {{ ownStatements.length }} · veřejná i soukromá.</p>
              <ol v-if="view.statements.length" class="sealed-entries sealed-messages">
                <li v-for="statement in view.statements" :key="statement.id">
                  <p><strong>{{ name(statement.actor) }}</strong> <span class="sealed-message-scope">{{ statement.public ? 'Veřejně' : 'Soukromě' }}</span></p>
                  <p>{{ statement.body }}</p>
                </li>
              </ol>
              <p v-else class="sealed-empty">Zatím tu nikdo nic netvrdil. Podezřelé ticho, ale samo o sobě nic nedokazuje.</p>
              <form v-if="view.ownState !== 'Operator'" class="sealed-message-form" @submit.prevent="sendMessage">
                <p v-if="shareStatus" role="status">{{ shareStatus }}</p>
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
          <details class="sealed-disclosure sealed-rules">
            <summary>Pravidla případu</summary>
            <ul class="sealed-help">
              <li>Máš soukromý cíl a na konci tajně hlasuješ podle vlastního úsudku.</li>
              <li>Každý tah má jednu hlavní akci a nanejvýš jeden sousední přesun. Návrh se uloží až po výslovném potvrzení; rozepsaná zpráva tah nemění.</li>
              <li>Vlastní stopa je důkaz; cizí zpráva je tvrzení; vlastní odhad je podezření. Úplný vlastní řetězec A/B/C umožní správný závěr.</li>
              <li>Rozpis A/B/C ukazuje místo a kapitolu uvolnění. K místu C je třeba dorazit před jeho uvolněním v kapitole 3.</li>
              <li>Ochrana sebe stojí tah a v této kapitole brání cizí sabotáži ve stejné místnosti. Bez vlastní oprávněné zkušenosti se nedozvíš, zda k útoku došlo.</li>
              <li>Zdržení jedné stopy může selhat a blokuje její nové získání do další kapitoly; již získané kopie zůstávají. V poslední kapitole po úspěchu nikdo nově nezíská tuto stopu pátráním před hlasováním.</li>
              <li>Soukromý telefon, role, spis a potvrzený plán patří hráči; neukazuj je na cizím telefonu ani na snímku obrazovky. Informace sdílej ústně nebo vědomě odeslanou zprávou.</li>
            </ul>
          </details>
        </aside>
      </template>
    </div>
  </SceneShell>
</template>

<style scoped src="./sealedCase.css"></style>
