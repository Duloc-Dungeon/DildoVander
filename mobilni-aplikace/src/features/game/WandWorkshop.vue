<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch, nextTick } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountOrigin, accountRequest, accountTransport } from '../identity/session';
import { secureUuid } from '../../application/identifiers.mjs';
import { projectMobileGeneratedWand } from '../archive/generatedWand';
import GeneratedWand from '../archive/GeneratedWand.vue';
import TrunkStage from './TrunkStage.vue';
import { LabVoice, type VoicePhase } from '../persona-lab/voice';
import { loadWorkshop, loadWorkshopSession, workshopSessionRequest, workshopBase, workshopSessionPath, workshopArtwork, normalizeSession, type WorkshopEntry, type WorkshopSession, type WorkshopMessage } from './wandWorkshop';

const props = defineProps<{ upgrade?: boolean; prelaunch?: boolean }>();
const emit = defineEmits<{ saved: []; exit: [] }>();
const entry = ref<WorkshopEntry>(); const session = ref<WorkshopSession | null>(null);
const loading = ref(true); const busy = ref(false); const error = ref(''); const draft = ref('');
const syncError = ref('');
const mode = ref<'voice' | 'text'>('voice'); const livePhase = ref<VoicePhase>('idle');
const muted = ref(false); const outputLevel = ref(0);
const partial = ref(''); let partialRole = ''; let partialComplete = false;
const artworkUrl = ref(''); const artworkError = ref('');
const answerField = ref<HTMLTextAreaElement>(); const correction = ref<WorkshopMessage | null>(null); const correctionText = ref('');
const title = ref<HTMLElement>();
const caption = ref<HTMLElement>();
const artworkDecoded = ref(false);
const newlyCompleted = ref(false);
let controller = new AbortController(); let poll: ReturnType<typeof setTimeout> | undefined;
let refreshInFlight: Promise<void> | null = null; let syncFailures = 0;
let operation = ''; let artworkKey = ''; let retryId = ''; let messageId = '';
const live = new LabVoice({
  phase(value) { livePhase.value = value; },
  transcript(role, delta) {
    if (partialRole !== role || partialComplete) { partial.value = ''; partialRole = role; }
    partialComplete = false; partial.value += delta;
  },
  turn(value) {
    if (value.role === partialRole) { partial.value = value.text; partialComplete = true; }
  },
  session() {},
  workshopSession(value) { applySession(normalizeSession(value as Parameters<typeof normalizeSession>[0])); },
  outputLevel(value) { outputLevel.value = value; },
  error(code) {
    error.value = code === 'wand_workshop_boss_captured' ? 'Baron uzavřel dílnu. Po osvobození navážeš na stejný rozhovor.'
      : code === 'wand_session_busy' ? 'Předchozí spojení ještě končí. Chvíli počkej a zkus pokračovat.'
      : code.startsWith('microphone') || code === 'permission_denied' ? 'Mikrofon není dostupný. Můžeš pokračovat psaním.'
      : 'Živé spojení se přerušilo. Můžeš ho obnovit nebo pokračovat psaním.';
    mode.value = 'text'; void refresh();
  },
  closed() { void refresh(); },
});
const wand = computed(() => projectMobileGeneratedWand(session.value?.wand));
watch(() => session.value?.status === 'complete' ? wand.value?.wandId : undefined, async id => {
  if (!id) return;
  emit('saved');
  await nextTick();
  title.value?.focus({ preventScroll: true });
});
const messages = computed(() => session.value?.messages ?? []);
const active = computed(() => ['connecting', 'listening', 'waiting', 'speaking'].includes(livePhase.value));
const speaking = computed(() => livePhase.value === 'speaking');
const canStart = computed(() => entry.value?.eligible === true && account.value?.openAiConsent === true);
const canAnswer = computed(() => canStart.value && session.value?.status === 'asking' && !busy.value);
const confirmedReply = computed(() => [...messages.value].reverse().find(message => message.role === 'assistant')?.text || session.value?.question?.text || '');
const subtitle = computed(() => partial.value || confirmedReply.value);
watch(subtitle, async () => {
  const viewport = caption.value;
  if (!viewport || viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight > 24) return;
  await nextTick();
  // Follow the current line inside the caption, without scrolling the page.
  if (caption.value === viewport) viewport.scrollTop = viewport.scrollHeight;
});
const unavailable = computed(() => {
  const reason = entry.value?.reason;
  if (!entry.value?.profile) return 'Nejdřív připrav svoji postavu.';
  if (reason === 'wand_character_preparing') return 'Ještě připravujeme tvoji postavu.';
  if (reason === 'wand_character_needs_clarification') return 'Postava potřebuje upřesnění. Doplň ji a dokonči přípravu.';
  if (reason === 'wand_character_preparation_failed') return 'Příprava postavy se nezdařila. Zkus ji opravit nebo zopakovat.';
  if (reason?.includes('boss') || reason?.includes('captur')) return 'Dílnu uzavřel Baron. Po jeho porážce budeš pokračovat ve stejném rozhovoru.';
  return entry.value && !entry.value.eligible ? 'Dílna teď není dostupná. Ověř svůj nárok ve hře.' : '';
});
const status = computed(() => {
  if (speaking.value) return 'Kufr mluví. Jeho řeč můžeš zastavit.';
  if (livePhase.value === 'connecting') return 'Připravujeme mikrofon a živé spojení…';
  if (active.value) return muted.value ? 'Mikrofon je ztlumený. Spojení zůstává aktivní.' : 'Mikrofon je zapnutý. Mluv přirozeně, kufr můžeš přerušit.';
  if (session.value?.status === 'generating') return '';
  if (session.value?.status === 'paused') return 'Rozhovor je pozastavený do osvobození dílny.';
  if (wand.value) return '';
  return busy.value ? 'Kufr zpracovává tvoje sdělení…' : mode.value === 'voice' ? 'Mikrofon je vypnutý.' : '';
});
// Presentation follows confirmed manufacturing state before transient voice state.
const stage = computed(() => wand.value ? 'revealed' : unavailable.value || session.value?.status === 'paused' ? 'unavailable'
  : session.value?.status === 'failed' ? 'error' : session.value?.status === 'generating' ? 'forging'
  : speaking.value ? 'speaking' : livePhase.value === 'connecting' ? 'connecting'
  : active.value && !muted.value && livePhase.value === 'listening' ? 'listening' : session.value ? 'waiting' : 'ready');
const activityLabel = computed(() => loading.value ? 'Ověřujeme dílnu' : ({
  ready: 'Kufr čeká na tebe', connecting: 'Navazujeme spojení', listening: 'Kufr naslouchá',
  speaking: 'Kufr má slovo', waiting: active.value ? muted.value ? 'Mikrofon je ztlumený' : 'Kufr chystá odpověď' : 'Rozhovor je uložený',
  forging: 'Kufr pracuje', revealed: 'Tvoje hůlka', unavailable: 'Dílna vyčkává', error: 'Výroba potřebuje pozornost',
})[stage.value]);
const journey = computed(() => wand.value ? 2 : session.value?.status === 'generating' || session.value?.status === 'failed' ? 1 : 0);
const artworkState = computed(() => artworkError.value ? 'failed' : session.value?.artwork?.status === 'ready' ? 'ready' : session.value?.artwork?.status === 'running' ? 'running' : session.value?.artwork?.status === 'failed' ? 'failed' : 'queued');
const artworkMessage = computed(() => {
  if (artworkError.value) return artworkError.value;
  if (artworkState.value === 'failed') return 'Ilustrace se nezdařila.';
  if (artworkState.value !== 'ready') return 'Ilustrace ještě vzniká. Nemusíš na ni čekat.';
  return artworkDecoded.value ? '' : 'Načítáme ilustraci…';
});
function stopSpeech() { if (active.value) live.interrupt(); outputLevel.value = 0; }
function stopMedia() { live.end(); live.dispose(); muted.value = false; outputLevel.value = 0; }
function releaseArtwork() { if (artworkUrl.value) URL.revokeObjectURL(artworkUrl.value); artworkUrl.value = ''; artworkKey = ''; artworkDecoded.value = false; }
async function updateArtwork(value: WorkshopSession) {
  const art = value.artwork; const scope = controller;
  if (!art?.assetId || !art.locator || art.status !== 'ready') return;
  const key = `${value.id}/${art.assetId}/${art.version}`; if (key === artworkKey) return; artworkKey = key;
  try {
    const blob = await workshopArtwork(`${workshopSessionPath(value.id)}/artwork/assets/${art.assetId}`, scope.signal);
    if (scope !== controller || scope.signal.aborted || artworkKey !== key) return;
    if (artworkUrl.value) URL.revokeObjectURL(artworkUrl.value);
    artworkDecoded.value = false;
    artworkUrl.value = URL.createObjectURL(blob); artworkError.value = '';
  } catch { if (scope === controller && !scope.signal.aborted) { artworkKey = ''; artworkError.value = 'Ilustraci se nepodařilo načíst.'; } }
}
function schedule() {
  clearTimeout(poll); if (controller.signal.aborted) return;
  const interval = syncFailures ? Math.min(30000, 3000 * 2 ** Math.min(syncFailures, 4))
    : active.value ? 10000 : entry.value?.reason?.startsWith('wand_character_') ? 2000 : 3000;
  poll = setTimeout(() => { void refresh(); }, interval);
}
function applySession(value: WorkshopSession | null) {
  // A restored result stays quiet; acknowledge only a completion observed here.
  if (value?.id !== session.value?.id) newlyCompleted.value = false;
  else if (value?.status === 'complete' && session.value?.status !== 'complete') newlyCompleted.value = !document.hidden;
  if (value?.id !== session.value?.id) { releaseArtwork(); retryId = ''; }
  session.value = value; if (value) { operation = value.id; void updateArtwork(value); }
  if (['generating', 'complete'].includes(value?.status ?? '') && active.value) { live.setMuted(true); muted.value = true; }
}
function refresh(): Promise<void> {
  if (refreshInFlight) return refreshInFlight;
  clearTimeout(poll);
  const scope = controller;
  const observedSession = session.value;
  const request = new AbortController();
  const cancel = () => request.abort();
  scope.signal.addEventListener('abort', cancel, { once: true });
  if (scope.signal.aborted) cancel();
  const deadline = setTimeout(cancel, 15000);
  const pending = (async () => {
    try {
      const value = await loadWorkshop(request.signal, props.upgrade);
      if (scope !== controller || scope.signal.aborted) return;
      const previousProfile = entry.value?.profile;
      if (previousProfile && (!value.profile || previousProfile.profileId !== value.profile.profileId
        || previousProfile.revision !== value.profile.revision)) {
        // The old operation and async requests belong to the old knowledge
        // revision. Keep its durable history, but do not reuse it for this one.
        resetWorkshopContext();
        entry.value = value; applySession(value.session); loading.value = false; schedule();
        return;
      }
      syncFailures = 0; syncError.value = ''; entry.value = value;
      // A socket event or command accepted during this GET is newer than its
      // snapshot. Keep that state; eligibility is still checked below.
      if (session.value === observedSession) applySession(value.session);
      if (!value.eligible) stopMedia();
    } catch {
      if (scope === controller && !scope.signal.aborted) {
        syncFailures++;
        syncError.value = active.value
          ? 'Aktualizace stavu se opozdila. Hlasové spojení pokračuje; stav zkusíme načíst znovu.'
          : 'Stav se nepodařilo načíst. Zkusíme ho znovu, nebo ho obnov tlačítkem.';
      }
    } finally {
      clearTimeout(deadline); scope.signal.removeEventListener('abort', cancel);
      if (scope === controller && !scope.signal.aborted) { loading.value = false; schedule(); }
    }
  })();
  refreshInFlight = pending;
  void pending.finally(() => { if (refreshInFlight === pending) refreshInFlight = null; });
  return pending;
}
async function ensureSession(): Promise<WorkshopSession> {
  if (session.value) return session.value;
  operation ||= secureUuid().replaceAll('-', '');
  const scope = controller;
  const value = await workshopSessionRequest(`${workshopBase}/sessions`, 'POST', { operationId: operation, upgrade: props.upgrade === true }, scope.signal);
  if (scope !== controller || scope.signal.aborted) throw new Error('Relace se změnila.');
  applySession(value); return value;
}
async function startVoice() {
  if (!canStart.value || busy.value || active.value || wand.value) return;
  mode.value = 'voice'; error.value = ''; partial.value = ''; partialComplete = false;
  const scope = controller;
  await live.connect(async () => {
    const current = await ensureSession();
    return accountRequest<import('../persona-lab/contracts').SocketTicket>(`${workshopSessionPath(current.id)}/ticket`, 'POST', { socketOrigin: window.location.origin }, scope.signal);
  }, () => session.value?.id ?? operation, accountOrigin.value || window.location.origin, { purpose: 'workshop' });
}
async function useText() {
  const scope = controller;
  stopMedia(); mode.value = 'text'; error.value = ''; partial.value = '';
  if (canStart.value && !session.value) {
    busy.value = true;
    try {
      const current = await ensureSession();
      if (!current.messages.length) {
        const value = await workshopSessionRequest(`${workshopSessionPath(current.id)}/messages`, 'POST', { messageId: current.id, text: '' }, scope.signal);
        if (scope === controller && !scope.signal.aborted) applySession(value);
      }
    } catch { error.value = 'Dílnu se nepodařilo otevřít. Obnov stav.'; }
    finally { if (scope === controller) busy.value = false; }
  }
  void nextTick(() => answerField.value?.focus());
}
async function submit() {
  if (!canAnswer.value || !draft.value.trim()) return;
  busy.value = true; error.value = ''; const scope = controller;
  messageId ||= secureUuid().replaceAll('-', '');
  try {
    const value = await workshopSessionRequest(`${workshopSessionPath(session.value!.id)}/messages`, 'POST', { messageId, text: draft.value.trim() }, scope.signal);
    if (scope !== controller || scope.signal.aborted) return;
    applySession(value); draft.value = ''; messageId = ''; partial.value = '';
  } catch { if (!scope.signal.aborted) { await refresh(); error.value = 'Stav byl ověřen. Pokud sdělení není uložené, odešli ho znovu; průběh zůstává zachovaný.'; } }
  finally { if (scope === controller) busy.value = false; }
}
async function finish() {
  if (!canAnswer.value) return;
  stopMedia(); busy.value = true; const scope = controller;
  try { const value = await workshopSessionRequest(`${workshopSessionPath(session.value!.id)}/finish`, 'POST', {}, scope.signal); if (scope === controller && !scope.signal.aborted) applySession(value); }
  catch { await refresh(); error.value = 'Dokončení se nepodařilo potvrdit. Ověř uložený stav a zkus to znovu.'; }
  finally { if (scope === controller) busy.value = false; }
}
async function saveCorrection() {
  if (!correction.value || !session.value || busy.value) return;
  busy.value = true; const scope = controller;
  try { const value = await workshopSessionRequest(`${workshopSessionPath(session.value.id)}/messages/${correction.value.messageId}`, 'PUT', { expectedRevision: correction.value.revision, text: correctionText.value }, scope.signal); if (scope === controller && !scope.signal.aborted) { applySession(value); correction.value = null; } }
  catch { error.value = 'Opravu se nepodařilo uložit. Obnov aktuální přepis.'; }
  finally { if (scope === controller) busy.value = false; }
}
async function retryManufacture() {
  if (!session.value || busy.value) return;
  busy.value = true; const scope = controller;
  try {
    const current = await loadWorkshopSession(session.value.id, scope.signal); if (scope !== controller || scope.signal.aborted) return; applySession(current);
    if (current.status === 'failed' && current.canRetry) { const value = await workshopSessionRequest(`${workshopSessionPath(current.id)}/retry`, 'POST', {}, scope.signal); if (scope === controller && !scope.signal.aborted) applySession(value); }
  } catch { error.value = 'Obnovení výroby se nepodařilo potvrdit. Ověř stejnou operaci.'; }
  finally { if (scope === controller) busy.value = false; }
}
async function artworkAction(repair = false) {
  if (!session.value || busy.value) return;
  busy.value = true; const scope = controller;
  try {
    const current = await loadWorkshopSession(session.value.id, scope.signal); if (scope !== controller || scope.signal.aborted) return; applySession(current);
    const art = current.artwork; retryId ||= secureUuid();
    if (repair && art?.canRepair || !repair && art?.canRetry) {
      const response = await accountTransport(`${workshopSessionPath(current.id)}/artwork/${repair ? 'repair' : 'retry'}`, { method: 'POST', signal: scope.signal, headers: { 'Content-Type': 'application/json', 'Idempotency-Key': retryId }, body: JSON.stringify(repair ? { expectedVersion: art!.version, sourceAssetId: art!.assetId, sourceSha256: art!.sha256 } : {}) });
      if (!response.ok) throw new Error('Ilustraci se nepodařilo obnovit.');
      retryId = ''; artworkError.value = '';
    } else { artworkKey = ''; await updateArtwork(current); }
    await refresh();
  } catch { artworkError.value = 'Ilustraci se nepodařilo obnovit.'; }
  finally { if (scope === controller) busy.value = false; }
}
function visibility() { if (document.hidden) stopMedia(); }
function resetWorkshopContext() {
  controller.abort(); controller = new AbortController(); refreshInFlight = null;
  syncFailures = 0; syncError.value = ''; error.value = ''; artworkError.value = '';
  stopMedia(); releaseArtwork(); session.value = null; entry.value = undefined;
  newlyCompleted.value = false;
  operation = ''; messageId = ''; retryId = ''; draft.value = ''; partial.value = ''; partialRole = ''; partialComplete = false;
  correction.value = null; correctionText.value = ''; mode.value = 'voice'; busy.value = false;
}
watch([() => account.value?.id, () => account.value?.openAiConsent, () => props.upgrade], () => {
  resetWorkshopContext();
  void refresh();
});
onMounted(() => { void refresh(); document.addEventListener('visibilitychange', visibility); });
onBeforeUnmount(() => { controller.abort(); clearTimeout(poll); stopMedia(); releaseArtwork(); document.removeEventListener('visibilitychange', visibility); });
</script>

<template>
  <section class="trunk-workshop" :data-result="!!wand" :data-new-wand="newlyCompleted" :data-mode="mode" :data-stage="stage" aria-labelledby="workshop-title">
    <header class="trunk-workshop__header"><AwButton v-if="prelaunch" @click="stopMedia(); emit('exit')">Zpět k odpočtu</AwButton><RouterLink v-else to="/play" @click="stopMedia">← Zpět do hry</RouterLink><RouterLink v-if="!prelaunch" to="/me/character" @click="stopMedia">Moje postava</RouterLink><span>Ollivander Industries™</span></header>
    <div class="trunk-workshop__identity">
      <h1 id="workshop-title" ref="title" tabindex="-1">{{ wand ? wand.name : upgrade ? 'Nová kapitola tvé hůlky' : 'Kufrova dílna' }}</h1>
      <p v-if="entry?.profile"><strong>{{ entry.profile.name }}</strong> · {{ entry.profile.universe }}</p>
    </div>
    <ol v-if="!loading && !unavailable" class="trunk-workshop__journey" aria-label="Průběh tvorby hůlky">
      <li v-for="(step, index) in ['Rozhovor', 'Výroba', 'Tvoje hůlka']" :key="step" :aria-current="journey === index ? 'step' : undefined" :data-complete="journey > index"><span>{{ step }}</span></li>
    </ol>
    <div class="trunk-workshop__scene" :data-artwork-visible="!wand || artworkState === 'ready'">
      <div class="trunk-workshop__presentation">
        <TrunkStage v-show="!wand || artworkState === 'ready'" :stage="stage" :activity-label="activityLabel" :assistant-output-level="outputLevel" :wand-artwork-url="artworkUrl" :wand-name="wand?.name" :artwork-status="artworkState" :show-caption="false" :show-artwork-status="false" @artwork-loaded="artworkDecoded = true" @artwork-error="artworkError = 'Ilustraci se nepodařilo načíst.'" />
        <div v-if="wand && (artworkMessage || session?.artwork?.canRepair)" class="trunk-workshop__artwork-notice">
          <p v-if="artworkMessage" role="status">{{ artworkMessage }}</p>
          <button v-if="session?.artwork?.canRetry || artworkError" type="button" class="trunk-workshop__text-action" :disabled="busy" @click="artworkAction()">Zkusit ilustraci znovu</button>
          <button v-if="session?.artwork?.canRepair" type="button" class="trunk-workshop__text-action" :disabled="busy" @click="artworkAction(true)">Opravit ilustraci na průhledném pozadí</button>
        </div>
      </div>
      <section class="trunk-workshop__conversation" :aria-label="wand ? 'Hotová hůlka' : 'Rozhovor s magickým kufrem'">
        <p class="trunk-workshop__announcement" aria-live="polite" aria-atomic="true">{{ mode === 'text' && session?.status === 'asking' ? confirmedReply : '' }}</p>
        <p v-if="loading" role="status">Ověřujeme tvoji postavu a dílnu…</p>
        <template v-else-if="unavailable"><p role="status">{{ unavailable }}</p><AwButton v-if="prelaunch" @click="stopMedia(); emit('exit')">Zpět k postavě</AwButton><RouterLink v-else class="aw-button aw-button--primary" to="/me/character">Upravit nebo dokončit přípravu postavy</RouterLink><RouterLink v-if="!prelaunch && entry?.reason?.includes('boss')" to="/play">Pomoci osvobodit dílnu</RouterLink></template>
        <template v-else-if="wand">
          <AwNotice class="trunk-workshop__saved" tone="success">{{ prelaunch ? 'Hůlka je uložená. Tvoje příprava je hotová.' : 'Hůlka je uložená a připravená ke hře.' }}</AwNotice>
          <p>{{ wand.material }} · {{ wand.core }}</p>
          <AwButton v-if="prelaunch" variant="primary" @click="stopMedia(); emit('exit')">Počkat na začátek večera</AwButton>
          <RouterLink v-else class="aw-button aw-button--primary" to="/play" @click="stopMedia">Pokračovat s hůlkou</RouterLink>
          <p v-if="session?.verdict" class="trunk-workshop__verdict">{{ session.verdict }}</p>
        </template>
        <template v-else-if="session?.status === 'generating'"><h2 class="trunk-workshop__greeting">Hůlka dostává tvar</h2><p role="status">Výroba poběží i po odchodu z obrazovky. Rozhovor už je uložený.</p><p class="trunk-workshop__hint">Až bude hotová, objeví se tady. Ilustrace se může dokončit později.</p></template>
        <template v-else-if="session?.status === 'failed'"><h2>Výroba se zastavila</h2><p>Rozhovor zůstává uložený. Obnov stav dílny{{ session.canRetry ? ' nebo zkus výrobu znovu' : '' }}.</p></template>
        <template v-else-if="session?.status === 'paused'"><h2>Dílna čeká na osvobození</h2><p>Rozhovor zůstává uložený. Po osvobození dílny můžeš navázat.</p></template>
        <template v-else>
          <h2 v-if="!session" class="trunk-workshop__greeting">Hůlka začíná rozhovorem.</h2>
          <p v-if="!session">Promluv si s kufrem o své hůlce. Lidem rozumí. Po staletí mu sedají na víko a tvrdí, že se tam ještě něco vejde.</p>
          <div v-if="session" ref="caption" class="trunk-workshop__caption" role="region" aria-label="Průběžný přepis" tabindex="0"><h2 class="trunk-workshop__question" :data-speaker="partial && partialRole === 'user' ? 'user' : 'assistant'">{{ subtitle }}</h2></div>
          <template v-if="mode === 'voice'"><AwButton v-if="!active && canStart" variant="primary" :disabled="busy" @click="startVoice">{{ session ? 'Pokračovat hlasem' : 'Promluvit s kufrem' }}</AwButton></template>
          <template v-else-if="session?.status === 'asking'">
            <form @submit.prevent="submit">
              <label for="trunk-answer">Tvoje sdělení kufru</label>
              <p id="trunk-answer-hint" class="trunk-workshop__hint">Stačí jedna věta. Třeba co od hůlky čekáš nebo čeho se má vyvarovat.</p>
              <textarea id="trunk-answer" ref="answerField" v-model="draft" aria-describedby="trunk-answer-hint" maxlength="2000" rows="3" :disabled="busy" />
              <AwButton type="submit" variant="primary" :disabled="!draft.trim() || busy">Odeslat</AwButton>
            </form>
            <button type="button" class="trunk-workshop__text-action" :disabled="!canStart || busy" @click="startVoice">Pokračovat hlasem</button>
          </template>
          <button v-if="canStart && !active && (mode === 'voice' || !session)" type="button" class="trunk-workshop__text-action" :disabled="busy" @click="useText">Raději psát</button>
          <div v-if="session?.status === 'asking' && mode === 'text'" class="trunk-workshop__finish">
            <p class="trunk-workshop__hint">Máš řečeno vše? Hůlka vznikne z uložených odpovědí.</p>
            <AwButton :disabled="busy" @click="finish">To stačí, vyrob hůlku</AwButton>
          </div>
        </template>
        <div v-if="active" class="trunk-workshop__media-controls">
          <AwButton :disabled="!speaking" @click="stopSpeech">Stop řeč</AwButton>
          <AwButton v-if="!wand" @click="muted = !muted; live.setMuted(muted)">{{ muted ? 'Zapnout mikrofon' : 'Ztlumit mikrofon' }}</AwButton>
          <button type="button" class="trunk-workshop__text-action" @click="stopMedia">Ukončit rozhovor</button>
          <button v-if="!wand && canStart" type="button" class="trunk-workshop__text-action" :disabled="busy" @click="useText">Raději psát</button>
          <p class="trunk-workshop__status" role="status">{{ status }}</p>
        </div>
        <details v-if="wand" class="trunk-workshop__properties"><summary>Vlastnosti a kouzla hůlky</summary><GeneratedWand :wand="session?.wand" mobile :show-artwork="false" /></details>
        <AwButton v-if="session?.status === 'failed' && session.canRetry" :disabled="busy" @click="retryManufacture">Obnovit výrobu</AwButton>
        <p v-if="status && !active && !loading && !unavailable" class="trunk-workshop__status" role="status">{{ status }}</p><p v-if="error" role="alert">{{ error }}</p><p v-if="syncError" role="status">{{ syncError }}</p>
        <AwButton v-if="error || syncError || session?.status === 'paused' || session?.status === 'failed'" :disabled="busy" @click="refresh">Obnovit stav</AwButton>
        <details v-if="messages.length" class="trunk-workshop__transcript"><summary>Přepis rozhovoru</summary><ol><li v-for="message in messages" :key="message.messageId"><strong>{{ message.role === 'user' ? 'Ty' : 'Kufr' }}</strong><p>{{ message.text }}</p><button v-if="mode === 'text' && canAnswer && message.role === 'user'" type="button" @click="correction = message; correctionText = message.text">Opravit sdělení</button></li></ol><form v-if="correction" @submit.prevent="saveCorrection"><label for="trunk-correction">Oprava sdělení</label><textarea id="trunk-correction" v-model="correctionText" maxlength="2000" /><AwButton type="submit" :disabled="busy || !correctionText.trim()">Uložit opravu</AwButton></form></details>
        <button v-if="prelaunch && session && !wand" type="button" class="trunk-workshop__text-action trunk-workshop__leave" @click="stopMedia(); emit('exit')">Odejít a později pokračovat</button>
        <RouterLink v-else-if="session && !wand" class="trunk-workshop__leave" to="/play" @click="stopMedia">Odejít a později pokračovat</RouterLink>
      </section>
    </div>
  </section>
</template>

<style scoped>
.trunk-workshop { box-sizing: border-box; width: min(100%, 68rem); margin: auto; padding: calc(.5rem + env(safe-area-inset-top, 0px)) 0 calc(1.5rem + env(safe-area-inset-bottom, 0px)); color: var(--wns-text); font-size: 1rem; overflow-wrap: anywhere; }
.trunk-workshop__header { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: .5rem 1rem; margin-bottom: 1rem; }
.trunk-workshop__header span { color: var(--wns-muted); font-size: .75rem; }
.trunk-workshop__identity h1 { margin: 0 0 .4rem; letter-spacing: -.03em; }
.trunk-workshop__identity p { margin: 0; }
.trunk-workshop .trunk-workshop__saved { margin: 0; padding-block: .5rem; }
.trunk-workshop__scene { display: grid; gap: 1rem; min-width: 0; margin-top: 1.25rem; }
.trunk-workshop__conversation { display: grid; align-content: start; gap: 1rem; min-width: 0; }
.trunk-workshop__hint { color: var(--wns-muted); font-size: .9rem; line-height: 1.5; }
.trunk-workshop__finish { display: grid; gap: .65rem; padding-top: 1rem; border-top: 1px solid var(--wns-line); }
.trunk-workshop__artwork-notice { display: grid; justify-items: center; gap: .5rem; padding: 1rem; text-align: center; }
.trunk-workshop__artwork-notice p { margin: 0; }
.trunk-workshop__artwork-notice .trunk-workshop__text-action { color: inherit; text-align: center; }
.trunk-workshop__conversation :is(p,h2) { margin: 0; }
.trunk-workshop__question { font-size: clamp(1.25rem, 5vw, 1.65rem); line-height: 1.4; font-weight: 650; }
.trunk-workshop__conversation progress { width: 100%; height: .3rem; accent-color: var(--wns-mint); }
.trunk-workshop__conversation form { display: grid; gap: .7rem; min-width: 0; }
.trunk-workshop textarea { box-sizing: border-box; width: 100%; min-width: 0; min-height: 6rem; resize: vertical; padding: .8rem; color: var(--wns-text); background: var(--wns-input-bg); border: 1px solid var(--wns-input-border); border-radius: var(--wns-radius-field); font: inherit; font-size: 1rem; line-height: 1.5; scroll-margin-bottom: 8rem; }
.trunk-workshop__text-action, .trunk-workshop__transcript button:not(.aw-button) { min-height: 44px; padding: .5rem 0; text-align: start; background: transparent; border: 0; color: var(--wns-mint); font: inherit; text-decoration: underline; text-underline-offset: .2em; cursor: pointer; }
.trunk-workshop__text-action:disabled { opacity: .48; cursor: not-allowed; }
.trunk-workshop__status { color: var(--wns-muted); font-size: .9rem; }
.trunk-workshop__transcript { border-top: 1px solid var(--wns-line); padding-top: 1rem; }
.trunk-workshop__transcript summary, .trunk-workshop__properties summary { min-height: 44px; cursor: pointer; }
.trunk-workshop__transcript ol { padding-left: 1.4rem; }
.trunk-workshop__transcript li { margin-bottom: 1rem; }
.trunk-workshop__transcript li p { white-space: pre-wrap; margin: .5rem 0; }
.trunk-workshop__leave { color: var(--wns-muted); min-height: 44px; display: inline-flex; align-items: center; }
.trunk-workshop__verdict { padding: 1.25rem; border-radius: var(--wns-radius-card); background: var(--wns-violet); color: var(--wns-on-violet); }
.trunk-workshop[data-result='true'] .trunk-workshop__presentation { display: grid; width: min(100%, 26rem); aspect-ratio: 1; margin-inline: auto; }
.trunk-workshop[data-result='true'] .trunk-workshop__presentation > * { grid-area: 1 / 1; align-self: center; min-width: 0; }
.trunk-workshop[data-result='true'] :deep(.trunk-stage__artwork) { width: 100%; min-height: 0; }
.trunk-workshop__announcement { position: absolute; width: 1px; height: 1px; padding: 0; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
/* The scene carries the expression; controls and durable state stay still. */
.trunk-workshop { padding-inline: clamp(1rem, 3vw, 2rem); }
.trunk-workshop ::selection { background: var(--wns-mint); color: var(--wns-on-mint); }
.trunk-workshop :is(a, button, textarea, summary):focus-visible { outline: 3px solid var(--wns-mint); outline-offset: 4px; }
.trunk-workshop textarea { caret-color: var(--wns-mint); }
.trunk-workshop__header { padding-bottom: .75rem; border-bottom: 1px solid var(--wns-line); }
.trunk-workshop__header a { display: inline-flex; min-height: 44px; align-items: center; text-underline-offset: .25em; }
.trunk-workshop__identity h1 { font-size: clamp(1.75rem, 4vw, 2.75rem); line-height: 1.15; text-wrap: balance; }
.trunk-workshop__identity p { color: var(--wns-muted); }
.trunk-workshop__identity strong { color: var(--wns-text); }
.trunk-workshop__journey { display: flex; list-style: none; gap: .75rem; margin: 1.5rem 0 0; padding: 0; }
.trunk-workshop__journey li { flex: 1; min-width: 0; padding-top: .65rem; border-top: 2px solid var(--wns-line); color: var(--wns-muted); font-size: .8rem; }
.trunk-workshop__journey li[aria-current] { border-color: var(--wns-mint); color: var(--wns-mint); font-weight: 760; }
.trunk-workshop__journey li[data-complete='true'] { border-color: var(--wns-mint); }
@media (prefers-reduced-motion: no-preference) {
  .trunk-workshop[data-new-wand='true'] .trunk-workshop__saved :deep(.aw-notice__icon) {
    animation: wand-sealed 480ms cubic-bezier(.16, 1, .3, 1);
  }
}
@keyframes wand-sealed {
  from { clip-path: inset(0 100% 0 0); transform: rotate(-12deg) scale(.85); }
  to { clip-path: inset(0); transform: none; }
}
.trunk-workshop__presentation { box-sizing: border-box; padding: .75rem; background: var(--wns-amber); color: var(--wns-on-amber); border-radius: var(--wns-radius-card); overflow: hidden; }
.trunk-workshop__presentation :deep(.trunk-stage) { color: inherit; }
.trunk-workshop[data-stage='forging'] .trunk-workshop__presentation, .trunk-workshop[data-result='true'] .trunk-workshop__presentation { background: var(--wns-violet); color: var(--wns-on-violet); }
.trunk-workshop[data-stage='unavailable'] .trunk-workshop__presentation, .trunk-workshop[data-stage='error'] .trunk-workshop__presentation { background: var(--wns-surface-raised); color: var(--wns-muted); }
.trunk-workshop__conversation { padding-block: .5rem; }
.trunk-workshop .trunk-workshop__conversation .trunk-workshop__greeting { font-size: clamp(1.5rem, 3vw, 2.2rem); line-height: 1.2; letter-spacing: -.025em; text-wrap: balance; }
.trunk-workshop__caption { block-size: 8rem; overflow-y: auto; overscroll-behavior-y: contain; scrollbar-gutter: stable; scrollbar-color: var(--wns-line) var(--wns-bg); border-bottom: 1px solid var(--wns-line); }
.trunk-workshop .trunk-workshop__conversation .trunk-workshop__question { font-size: clamp(1.2rem, 2.5vw, 1.65rem); padding-block: .5rem 1rem; }
.trunk-workshop__question[data-speaker='user'] { color: var(--wns-muted); font-weight: 500; }
.trunk-workshop__media-controls { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); align-items: stretch; gap: .5rem 1rem; padding-block: .5rem; border-top: 1px solid var(--wns-line); }
.trunk-workshop__media-controls > :is(button, .aw-button) { min-height: 3.5rem; }
.trunk-workshop__media-controls .trunk-workshop__status { grid-column: 1 / -1; min-block-size: 4.5em; }
.trunk-workshop__transcript { scrollbar-color: var(--wns-line) var(--wns-bg); }
.trunk-workshop__transcript summary, .trunk-workshop__properties summary { display: list-item; align-content: center; }
.trunk-workshop__transcript ol { list-style: none; padding: 0; }
.trunk-workshop__transcript li { border-bottom: 1px solid var(--wns-line); padding-block: .75rem; margin: 0; }
.trunk-workshop__transcript li strong { color: var(--wns-mint); font-size: .85rem; }
.trunk-workshop .aw-button--primary { box-shadow: none; border: 0; min-height: 56px; }
@media (min-width: 850px) { .trunk-workshop__scene { grid-template-columns: minmax(0, 1.05fr) minmax(0, 1fr); gap: 2.5rem; margin-top: 2rem; align-items: start; } .trunk-workshop__presentation { position: sticky; top: 1rem; } }
@media (max-width: 849px) {
  .trunk-workshop :deep(.trunk-stage__visual) { width: min(100%, 9rem); margin: 0; flex: 0 1 9rem; }
  .trunk-workshop[data-result='false'] .trunk-workshop__presentation :deep(.trunk-stage) { display: flex; align-items: center; justify-content: center; gap: 1rem; }
  .trunk-workshop :deep(.trunk-stage__activity) { flex-direction: column; gap: .25rem; text-align: center; min-width: 0; flex: 0 1 10rem; min-height: 5rem; }
  .trunk-workshop[data-result='true'] .trunk-workshop__presentation { width: min(100%, 14rem); }
}
</style>
