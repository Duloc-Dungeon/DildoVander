<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch, nextTick } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import { account, accountOrigin, accountRequest, accountTransport } from '../identity/session';
import { secureUuid } from '../../application/identifiers.mjs';
import { projectMobileGeneratedWand } from '../archive/generatedWand';
import GeneratedWand from '../archive/GeneratedWand.vue';
import TrunkStage from './TrunkStage.vue';
import { LabVoice, type VoicePhase } from '../persona-lab/voice';
import { WorkshopVoice, type WorkshopVoicePhase } from './workshopVoice';
import { loadWorkshop, loadWorkshopSession, workshopSessionRequest, workshopBase, workshopSessionPath, workshopArtwork, workshopAudio, normalizeSession, type WorkshopEntry, type WorkshopSession, type WorkshopMessage } from './wandWorkshop';

const props = defineProps<{ upgrade?: boolean }>();
const entry = ref<WorkshopEntry>(); const session = ref<WorkshopSession | null>(null);
const loading = ref(true); const busy = ref(false); const error = ref(''); const draft = ref('');
const syncError = ref('');
const mode = ref<'voice' | 'text'>('voice'); const livePhase = ref<VoicePhase>('idle');
const narrationPhase = ref<WorkshopVoicePhase>('off'); const narrationPending = ref(false);
let mediaController = new AbortController(); const muted = ref(false); const outputLevel = ref(0);
const partial = ref(''); let partialRole = ''; const artworkUrl = ref(''); const artworkError = ref('');
const answerField = ref<HTMLTextAreaElement>(); const correction = ref<WorkshopMessage | null>(null); const correctionText = ref('');
let controller = new AbortController(); let poll: ReturnType<typeof setTimeout> | undefined;
let refreshInFlight: Promise<void> | null = null; let syncFailures = 0;
let operation = ''; let artworkKey = ''; let retryId = ''; let messageId = '';
const narration = new WorkshopVoice({ phase(value) { narrationPhase.value = value; }, level(value) { outputLevel.value = value; } });
const live = new LabVoice({
  phase(value) { livePhase.value = value; },
  transcript(role, delta) { if (partialRole !== role) { partial.value = ''; partialRole = role; } partial.value += delta; },
  turn() {}, session() {},
  workshopSession(value) { applySession(normalizeSession(value as Parameters<typeof normalizeSession>[0])); },
  outputLevel(value) { outputLevel.value = value; },
  error(code) {
    error.value = code === 'wand_workshop_boss_captured' ? 'Baron uzavřel dílnu. Po osvobození navážeš na stejný rozhovor.'
      : code === 'wand_session_busy' ? 'Předchozí spojení ještě ukládá rozpracovanou odpověď. Chvíli počkej a pokračuj ve stejném rozhovoru.'
      : code.startsWith('microphone') || code === 'permission_denied' ? 'Mikrofon není dostupný. Můžeš pokračovat psaním.'
      : 'Živé spojení se přerušilo. Můžeš ho obnovit nebo pokračovat psaním.';
    mode.value = 'text'; void refresh();
  },
  closed() { void refresh(); },
});
const wand = computed(() => projectMobileGeneratedWand(session.value?.wand));
const messages = computed(() => session.value?.messages ?? []);
const active = computed(() => ['connecting', 'listening', 'waiting', 'speaking'].includes(livePhase.value));
const speaking = computed(() => livePhase.value === 'speaking' || narrationPhase.value === 'speaking');
const canStart = computed(() => entry.value?.eligible === true && account.value?.openAiConsent === true);
const canAnswer = computed(() => canStart.value && session.value?.status === 'asking' && !busy.value);
const subtitle = computed(() => partial.value || [...messages.value].reverse().find(message => message.role === 'assistant')?.text || session.value?.question?.text || '');
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
  if (active.value) return muted.value ? 'Mikrofon je ztlumený. Spojení zůstává aktivní.' : 'Mikrofon je zapnutý. Mluv přirozeně, kufra můžeš přerušit.';
  if (session.value?.status === 'generating') return 'Rozhovor je uložený. Kufr vyrábí tvoji hůlku.';
  if (session.value?.status === 'paused') return 'Rozhovor je pozastavený do osvobození dílny.';
  if (wand.value) return 'Hůlka je uložená a připravená ke hře.';
  return busy.value ? 'Kufr zpracovává tvoje sdělení…' : 'Mikrofon je vypnutý.';
});
const stage = computed(() => wand.value ? 'revealed' : unavailable.value ? 'unavailable' : session.value?.status === 'generating' ? 'forging' : speaking.value ? 'speaking' : livePhase.value === 'connecting' ? 'connecting' : active.value ? 'listening' : session.value ? 'waiting' : 'ready');
const artworkState = computed(() => artworkError.value ? 'failed' : session.value?.artwork?.status === 'ready' ? 'ready' : session.value?.artwork?.status === 'running' ? 'running' : session.value?.artwork?.status === 'failed' ? 'failed' : 'queued');
function stopNarration() { mediaController.abort(); mediaController = new AbortController(); narrationPending.value = false; narration.stop(); }
function stopSpeech() { if (active.value) live.interrupt(); stopNarration(); outputLevel.value = 0; }
function stopMedia() { live.end(); live.dispose(); stopNarration(); muted.value = false; outputLevel.value = 0; }
function releaseArtwork() { if (artworkUrl.value) URL.revokeObjectURL(artworkUrl.value); artworkUrl.value = ''; artworkKey = ''; }
async function updateArtwork(value: WorkshopSession) {
  const art = value.artwork; const scope = controller;
  if (!art?.assetId || !art.locator || art.status !== 'ready') return;
  const key = `${value.id}/${art.assetId}/${art.version}`; if (key === artworkKey) return; artworkKey = key;
  try {
    const blob = await workshopArtwork(`/app/v1/archive/owned/${art.locator}/assets/${art.assetId}`, scope.signal);
    if (scope !== controller || scope.signal.aborted || artworkKey !== key) return;
    if (artworkUrl.value) URL.revokeObjectURL(artworkUrl.value);
    artworkUrl.value = URL.createObjectURL(blob); artworkError.value = '';
  } catch { if (scope === controller && !scope.signal.aborted) { artworkKey = ''; artworkError.value = 'Ilustraci se nepodařilo načíst. Hůlka zůstává uložená.'; } }
}
function schedule() {
  clearTimeout(poll); if (controller.signal.aborted) return;
  const interval = syncFailures ? Math.min(30000, 3000 * 2 ** Math.min(syncFailures, 4))
    : active.value ? 10000 : entry.value?.reason?.startsWith('wand_character_') ? 2000 : 3000;
  poll = setTimeout(() => { void refresh(); }, interval);
}
function applySession(value: WorkshopSession | null) {
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
  mode.value = 'voice'; error.value = ''; narration.stop(); partial.value = '';
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
async function speak() {
  if (!session.value || !wand.value || narrationPending.value) return;
  stopMedia(); const scope = controller; const media = mediaController; narrationPending.value = true;
  try { await narration.unlock(); const blob = await workshopAudio(session.value.id, { wandId: wand.value.wandId }, media.signal); if (scope === controller && !scope.signal.aborted && !media.signal.aborted) await narration.play(blob); }
  catch { if (!media.signal.aborted && scope === controller) error.value = 'Přednes není dostupný. Verdikt je stále čitelný.'; }
  finally { if (media === mediaController) narrationPending.value = false; }
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
      retryId = '';
    } else { artworkKey = ''; await updateArtwork(current); }
    await refresh();
  } catch { artworkError.value = 'Ilustraci se nepodařilo obnovit. Tvoje hůlka zůstává uložená.'; }
  finally { if (scope === controller) busy.value = false; }
}
function visibility() { if (document.hidden) stopMedia(); }
watch(() => [account.value?.id, account.value?.openAiConsent, props.upgrade], () => { controller.abort(); controller = new AbortController(); refreshInFlight = null; syncFailures = 0; syncError.value = ''; error.value = ''; stopMedia(); releaseArtwork(); session.value = null; entry.value = undefined; operation = ''; draft.value = ''; busy.value = false; void refresh(); });
onMounted(() => { void refresh(); document.addEventListener('visibilitychange', visibility); });
onBeforeUnmount(() => { controller.abort(); clearTimeout(poll); stopMedia(); releaseArtwork(); document.removeEventListener('visibilitychange', visibility); });
</script>

<template>
  <section class="trunk-workshop" aria-labelledby="workshop-title">
    <header class="trunk-workshop__header"><RouterLink to="/play" @click="stopMedia">← Zpět do hry</RouterLink><span>Wand Night Stand</span></header>
    <div class="trunk-workshop__identity"><p class="trunk-workshop__eyebrow">{{ upgrade ? 'Nová kapitola tvé hůlky' : 'Tvoje první hůlka' }}</p><h1 id="workshop-title">Kufrova dílna</h1><p v-if="entry?.profile"><strong>{{ entry.profile.name }}</strong> · {{ entry.profile.universe }}</p></div>
    <div class="trunk-workshop__scene">
      <TrunkStage :stage="stage" :assistant-output-level="outputLevel" :wand-artwork-url="artworkUrl" :wand-name="wand?.name" :artwork-status="artworkState" />
      <section class="trunk-workshop__conversation" aria-label="Rozhovor s magickým kufrem">
        <p v-if="loading" role="status">Ověřujeme tvoji postavu a dílnu…</p>
        <template v-else-if="unavailable"><p role="status">{{ unavailable }}</p><RouterLink to="/me/character">Upravit nebo dokončit přípravu postavy</RouterLink><RouterLink v-if="entry?.reason?.includes('boss')" to="/play">Pomoci osvobodit dílnu</RouterLink></template>
        <template v-else-if="wand">
          <p class="trunk-workshop__eyebrow">Hůlka je uložená</p><p>{{ wand.material }} · {{ wand.core }}</p><p v-if="session?.verdict" class="trunk-workshop__verdict">{{ session.verdict }}</p>
          <RouterLink class="aw-button aw-button--primary" to="/play" @click="stopMedia">Pokračovat s hůlkou</RouterLink>
          <AwButton v-if="!speaking" :disabled="narrationPending" @click="speak">Přehrát verdikt kufru</AwButton>
          <p v-if="session?.artwork?.status !== 'ready'" role="status">{{ session?.artwork?.status === 'failed' ? 'Ilustrace se nezdařila. Hůlka je stále připravená ke hře.' : 'Ilustrace ještě vzniká. Můžeš pokračovat do hry.' }}</p>
          <AwButton v-if="session?.artwork?.canRetry || artworkError" :disabled="busy" @click="artworkAction()">Zkusit ilustraci znovu</AwButton>
          <AwButton v-if="session?.artwork?.canRepair" :disabled="busy" @click="artworkAction(true)">Opravit ilustraci na průhledném pozadí</AwButton>
          <details class="trunk-workshop__properties"><summary>Vlastnosti a kouzla hůlky</summary><GeneratedWand :wand="session?.wand" mobile :show-artwork="false" /></details>
        </template>
        <template v-else-if="session?.status === 'generating'"><h2>Hůlka dostává tvar</h2><p>Výroba poběží i po odchodu z obrazovky. Rozhovor už je uložený.</p></template>
        <template v-else>
          <p v-if="!session">Kufr zná připravenou postavu. Promluv si s ním o hůlce, která k ní patří.</p>
          <h2 v-if="subtitle" class="trunk-workshop__question" aria-live="polite">{{ subtitle }}</h2>
          <template v-if="mode === 'voice'"><AwButton v-if="!active && canStart" variant="primary" :disabled="busy" @click="startVoice">{{ session ? 'Pokračovat hlasem' : 'Promluvit s kufrem' }}</AwButton><AwButton v-if="active" @click="muted = !muted; live.setMuted(muted)">{{ muted ? 'Zapnout mikrofon' : 'Ztlumit mikrofon' }}</AwButton></template>
          <template v-else-if="session?.status === 'asking'"><form @submit.prevent="submit"><label for="trunk-answer">Tvoje sdělení kufru</label><textarea id="trunk-answer" ref="answerField" v-model="draft" maxlength="2000" rows="3" :disabled="busy" /><AwButton type="submit" variant="primary" :disabled="!draft.trim() || busy">Odeslat</AwButton></form><AwButton :disabled="!canStart || busy" @click="startVoice">Pokračovat hlasem</AwButton></template>
          <AwButton v-if="canStart && (mode === 'voice' || !session)" :disabled="busy" @click="useText">Raději psát</AwButton>
          <AwButton v-if="session?.status === 'asking' && mode === 'text'" :disabled="busy" @click="finish">To stačí, vyrob hůlku</AwButton>
        </template>
        <AwButton v-if="session?.status === 'failed' && session.canRetry" :disabled="busy" @click="retryManufacture">Obnovit výrobu</AwButton>
        <AwButton v-if="active" @click="stopMedia">Ukončit rozhovor</AwButton>
        <AwButton v-if="speaking || narrationPending" @click="stopSpeech">Stop řeč</AwButton>
        <p class="trunk-workshop__status" role="status">{{ status }}</p><p v-if="error" role="alert">{{ error }}</p><p v-if="syncError" role="status">{{ syncError }}</p><p v-if="artworkError" role="status">{{ artworkError }}</p>
        <AwButton v-if="error || syncError || session?.status === 'paused' || session?.status === 'failed'" :disabled="busy" @click="refresh">Obnovit stav</AwButton>
        <details v-if="messages.length" class="trunk-workshop__transcript"><summary>Přepis rozhovoru</summary><ol><li v-for="message in messages" :key="message.messageId"><strong>{{ message.role === 'user' ? 'Ty' : 'Kufr' }}</strong><p>{{ message.text }}</p><button v-if="mode === 'text' && canAnswer && message.role === 'user'" type="button" @click="correction = message; correctionText = message.text">Opravit sdělení</button></li></ol><form v-if="correction" @submit.prevent="saveCorrection"><label for="trunk-correction">Oprava sdělení</label><textarea id="trunk-correction" v-model="correctionText" maxlength="2000" /><AwButton type="submit" :disabled="busy || !correctionText.trim()">Uložit opravu</AwButton></form></details>
        <RouterLink v-if="session && !wand" class="trunk-workshop__leave" to="/play" @click="stopMedia">Odejít a později pokračovat</RouterLink>
      </section>
    </div>
  </section>
</template>

<style scoped>
.trunk-workshop { box-sizing: border-box; width: min(100%, 68rem); margin: auto; padding: calc(1rem + env(safe-area-inset-top, 0px)) clamp(.75rem, 3vw, 2rem) calc(6rem + env(safe-area-inset-bottom, 0px)); color: var(--wns-text); font-size: 1rem; overflow-wrap: anywhere; }
.trunk-workshop__header { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 1rem; margin-bottom: 1.75rem; }
.trunk-workshop__header span { color: var(--wns-muted); font-size: .75rem; }
.trunk-workshop__identity h1 { margin: .3rem 0 .75rem; }
.trunk-workshop__identity p { margin: 0; }
.trunk-workshop__eyebrow { color: var(--wns-mint); text-transform: uppercase; font-size: .8rem; letter-spacing: .07em; }
.trunk-workshop__scene { display: grid; gap: 1rem; min-width: 0; }
.trunk-workshop__conversation { display: grid; align-content: start; gap: 1rem; min-width: 0; }
.trunk-workshop__conversation :is(p,h2) { margin: 0; }
.trunk-workshop__question { font-size: clamp(1.25rem, 5vw, 1.65rem); line-height: 1.4; font-weight: 650; }
.trunk-workshop__conversation progress { width: 100%; height: .3rem; accent-color: var(--wns-mint); }
.trunk-workshop__conversation form { display: grid; gap: .7rem; min-width: 0; }
.trunk-workshop textarea { box-sizing: border-box; width: 100%; min-width: 0; min-height: 6rem; resize: vertical; padding: .8rem; color: var(--wns-text); background: var(--wns-input-bg); border: 1px solid var(--wns-input-border); border-radius: var(--wns-radius-field); font: inherit; font-size: 1rem; line-height: 1.5; scroll-margin-bottom: 8rem; }
.trunk-workshop__voice-controls { display: grid; gap: .65rem; }
.trunk-workshop__mode button, .trunk-workshop__skip, .trunk-workshop__transcript button:not(.aw-button) { min-height: 44px; padding: .5rem 0; text-align: start; background: transparent; border: 0; color: var(--wns-mint); font: inherit; text-decoration: underline; cursor: pointer; }
.trunk-workshop__skip { color: var(--wns-muted); }
.trunk-workshop__status { color: var(--wns-muted); font-size: .9rem; }
.trunk-workshop__status span { display: inline-block; margin-right: .5rem; width: .5rem; height: .5rem; border-radius: 50%; background: var(--wns-muted); }
.trunk-workshop__status span[data-active='true'] { background: var(--wns-mint); }
.trunk-workshop__transcript { border-top: 1px solid var(--wns-line); padding-top: 1rem; }
.trunk-workshop__transcript summary, .trunk-workshop__properties summary { min-height: 44px; cursor: pointer; }
.trunk-workshop__transcript ol { padding-left: 1.4rem; }
.trunk-workshop__transcript li { margin-bottom: 1rem; }
.trunk-workshop__transcript li p { white-space: pre-wrap; margin: .5rem 0; }
.trunk-workshop__leave { color: var(--wns-muted); min-height: 44px; display: inline-flex; align-items: center; }
.trunk-workshop__verdict { padding: 1.25rem; border-radius: var(--wns-radius-card); background: var(--wns-violet); color: var(--wns-on-violet); }
@media (min-width: 850px) { .trunk-workshop__scene { grid-template-columns: minmax(0, 1.05fr) minmax(0, 1fr); gap: 2.5rem; margin-top: 2rem; align-items: start; } }
@media (max-width: 380px) { .trunk-workshop__header { margin-bottom: 1rem; } }
@media (max-width: 849px) { .trunk-workshop :deep(.trunk-stage__visual) { max-height: min(32dvh, 20rem); } }
@media (max-height: 560px) and (max-width: 849px) { .trunk-workshop :deep(.trunk-stage__visual) { max-height: 18dvh; } }
</style>
