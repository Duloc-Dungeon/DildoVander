<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import { account, accountTransport } from '../identity/session';
import { secureUuid } from '../../application/identifiers.mjs';
import { projectMobileGeneratedWand } from '../archive/generatedWand';
import GeneratedWand from '../archive/GeneratedWand.vue';
import TrunkStage from './TrunkStage.vue';
import { WorkshopVoice, type WorkshopVoicePhase } from './workshopVoice';
import { loadWorkshop, loadWorkshopSession, workshopSessionRequest, workshopBase, workshopSessionPath, workshopAudio, WorkshopAudioConflict,
  transcribeWorkshopAudio, workshopArtwork, type WorkshopEntry, type WorkshopSession, type WorkshopStep } from './wandWorkshop';

const props = defineProps<{ upgrade?: boolean }>();
const entry = ref<WorkshopEntry>();
const session = ref<WorkshopSession | null>(null);
const busy = ref(false);
const loading = ref(true);
const error = ref('');
const draft = ref('');
const sourceMode = ref<'text' | 'voice'>('text');
const mode = ref<'text' | 'voice'>('text');
const voicePhase = ref<WorkshopVoicePhase>('off');
const microphonePending = ref(false);
const outputLevel = ref(0);
const artworkUrl = ref('');
const artworkError = ref('');
const correction = ref<WorkshopStep | null>(null);
const correctionText = ref('');
const answerField = ref<HTMLTextAreaElement>();
const questionHeading = ref<HTMLHeadingElement>();
const operation = ref('');
let controller = new AbortController();
let mediaController = new AbortController();
let epoch = 0;
let poll: ReturnType<typeof setTimeout> | undefined;
let loadedArtwork = '';
let artworkRetryId = '';
const voice = new WorkshopVoice({
  phase(value) { voicePhase.value = value; if (value === 'listening') microphonePending.value = false; },
  level(value) { outputLevel.value = value; },
});
const question = computed(() => session.value?.question);
const wand = computed(() => projectMobileGeneratedWand(session.value?.wand));
const completedSteps = computed(() => session.value?.steps.filter(step => Boolean(step.answerId) || step.skipped) ?? []);
const canStart = computed(() => entry.value?.eligible && account.value?.openAiConsent === true);
const canAnswer = computed(() => question.value && session.value?.status === 'asking' && entry.value?.eligible && !busy.value);
const status = computed(() => {
  if (microphonePending.value) return 'Čekáme na povolení mikrofonu.';
  if (voicePhase.value === 'listening') return 'Mikrofon je zapnutý. Nahrává se tvoje odpověď.';
  if (voicePhase.value === 'speaking') return 'Kufr mluví. Přednes můžeš zastavit.';
  if (voicePhase.value === 'processing') return 'Přepisujeme nahrávku. Odpověď ještě není odeslaná.';
  if (busy.value) return 'Zpracováváme požadavek…';
  if (session.value?.status === 'authoring') return 'Kufr připravuje další otázku podle tvé postavy a uložených odpovědí.';
  if (session.value?.status === 'generating') return 'Tři odpovědi jsou uložené. Kufr vyrábí tvoji hůlku.';
  if (wand.value) return 'Hůlka je uložená a připravená ke hře.';
  return 'Mikrofon je vypnutý.';
});
const unavailable = computed(() => {
  const reason = entry.value?.reason;
  if (!entry.value?.profile) return 'Nejdřív ulož svoji postavu.';
  if (reason?.includes('captur') || reason?.includes('boss')) return 'Dílnu uzavřel Baron. Znovu se otevře po jeho porážce.';
  if (entry.value && !entry.value.eligible && !wand.value) return 'Dílna teď není dostupná. Vrať se do hry a ověř svůj nárok.';
  return '';
});
const stage = computed(() => {
  if (wand.value) return 'revealed';
  if (unavailable.value) return 'unavailable';
  if (session.value?.status === 'generating') return 'forging';
  if (voicePhase.value === 'speaking') return 'speaking';
  if (voicePhase.value === 'listening') return 'listening';
  return session.value ? 'waiting' : 'ready';
});
const artworkState = computed(() => {
  if (artworkError.value) return 'failed';
  const state = session.value?.artwork?.status;
  if (state === 'ready' || state === 'running') return state;
  return state === 'queued' || !wand.value ? 'queued' : 'failed';
});

function stopMedia() {
  mediaController.abort(); mediaController = new AbortController();
  microphonePending.value = false; voice.stop();
}
function releaseArtwork() {
  if (artworkUrl.value) URL.revokeObjectURL(artworkUrl.value);
  artworkUrl.value = ''; loadedArtwork = '';
}
async function updateArtwork(value: WorkshopSession) {
  const scope = controller;
  const art = value.artwork;
  if (art?.status !== 'ready' || !art.assetId || !art.locator) return;
  const key = `${value.id}/${art.assetId}/${art.version}`;
  if (key === loadedArtwork) return;
  loadedArtwork = key;
  try {
    const blob = await workshopArtwork(`/app/v1/archive/owned/${encodeURIComponent(art.locator)}/assets/${encodeURIComponent(art.assetId)}`, scope.signal);
    if (!currentScope(scope) || session.value?.id !== value.id || loadedArtwork !== key) return;
    if (artworkUrl.value) URL.revokeObjectURL(artworkUrl.value);
    artworkUrl.value = URL.createObjectURL(blob); artworkError.value = '';
  } catch {
    if (currentScope(scope)) { loadedArtwork = ''; artworkError.value = 'Ilustraci se nepodařilo načíst. Hůlka zůstává uložená.'; }
  }
}
function applySession(value: WorkshopSession | null) {
  const changedQuestion = value?.question?.id !== session.value?.question?.id;
  if (value?.id !== session.value?.id) { releaseArtwork(); artworkRetryId = ''; }
  if (value?.question?.id !== session.value?.question?.id || value?.id !== session.value?.id) {
    stopMedia(); draft.value = ''; sourceMode.value = 'text'; correction.value = null;
  }
  session.value = value;
  if (changedQuestion && value?.question) void nextTick(() => questionHeading.value?.focus());
  if (value) { operation.value = value.id; void updateArtwork(value); }
  schedulePoll();
}
function schedulePoll() {
  clearTimeout(poll);
  const value = session.value;
  if (controller.signal.aborted || !value) return;
  if (['authoring', 'generating'].includes(value.status) || value.status === 'complete' && value.artwork && ['queued', 'running', 'pending'].includes(value.artwork.status)) {
    poll = setTimeout(() => { void refreshSession(); }, 2500);
  }
}
async function refreshSession() {
  if (!session.value) return;
  const scope = controller;
  try { const value = await loadWorkshopSession(session.value.id, scope.signal); if (currentScope(scope)) applySession(value); }
  catch { if (currentScope(scope)) error.value = 'Stav se nepodařilo ověřit. Obnov jej před dalším pokusem.'; }
}
function currentScope(scope: AbortController) { return scope === controller && !scope.signal.aborted; }
async function refresh() {
  const current = ++epoch;
  const scope = controller;
  loading.value = true; error.value = '';
  try {
    const value = await loadWorkshop(scope.signal, props.upgrade === true);
    if (current !== epoch || !currentScope(scope)) return;
    entry.value = value; if (!value.eligible) stopMedia(); applySession(value.session);
  } catch { if (currentScope(scope) && current === epoch) error.value = 'Dílnu se nepodařilo načíst. Zkus obnovit stav.'; }
  finally { if (current === epoch) loading.value = false; }
}
async function start() {
  if (!canStart.value || busy.value || session.value) return;
  const scope = controller;
  busy.value = true; error.value = '';
  operation.value ||= secureUuid().replaceAll('-', '');
  try {
    const value = await workshopSessionRequest(`${workshopBase}/sessions`, 'POST', { operationId: operation.value, upgrade: props.upgrade === true }, scope.signal);
    if (currentScope(scope)) applySession(value);
  } catch { if (currentScope(scope)) { await refresh(); if (currentScope(scope)) error.value ||= 'Ověřili jsme stav výroby. Pokud rozhovor nezačal, můžeš to zkusit znovu.'; } }
  finally { if (currentScope(scope)) busy.value = false; }
}
async function submit(skip = false) {
  const active = question.value;
  const current = session.value;
  if (!canAnswer.value || !active || !current || !skip && !draft.value.trim()) return;
  const scope = controller;
  stopMedia(); busy.value = true; error.value = '';
  try {
    const body = { questionId: active.id, questionRevision: active.revision, ...(skip ? {} : { answer: draft.value.trim(), sourceMode: sourceMode.value }) };
    const value = await workshopSessionRequest(`${workshopSessionPath(current.id)}/${skip ? 'skip' : 'answers'}`, 'POST', body, scope.signal);
    if (currentScope(scope)) applySession(value);
  } catch {
    if (currentScope(scope)) { await refresh(); if (currentScope(scope)) error.value ||= 'Ověřili jsme uložený postup. Pokud je otázka stále aktivní, odešli odpověď znovu.'; }
  } finally { if (currentScope(scope)) busy.value = false; }
}
async function speak() {
  if (busy.value || !session.value || !question.value && !wand.value) return;
  const scope = controller;
  stopMedia(); const signal = mediaController.signal;
  busy.value = true; error.value = '';
  try {
    await voice.unlock();
    const proof = question.value ? { questionId: question.value.id, questionRevision: question.value.revision } : { wandId: wand.value!.wandId };
    const audio = await workshopAudio(session.value.id, proof, signal);
    if (!signal.aborted) await voice.play(audio);
  } catch (cause) {
    if (!signal.aborted && cause instanceof WorkshopAudioConflict) { await refresh(); error.value = 'Otázka se mezitím změnila. Zobrazujeme aktuální postup.'; }
    else if (!signal.aborted) error.value = 'Přednes se nepodařilo přehrát. Titulky zůstávají dostupné.';
  }
  finally { if (currentScope(scope)) busy.value = false; }
}
async function record() {
  const active = question.value;
  if (!canAnswer.value || !active || voicePhase.value === 'listening' || microphonePending.value) return;
  stopMedia(); const signal = mediaController.signal;
  mode.value = 'voice'; microphonePending.value = true; error.value = '';
  try {
    const audio = await voice.record();
    if (!audio || signal.aborted || !session.value) return;
    microphonePending.value = false;
    voicePhase.value = 'processing';
    const text = await transcribeWorkshopAudio(session.value.id, active, audio, signal);
    if (signal.aborted) return;
    draft.value = text.trim(); sourceMode.value = 'voice';
    if (!draft.value) error.value = 'Nahrávka neobsahuje srozumitelnou odpověď. Zkus to znovu nebo odpověz psaním.';
    await nextTick(); answerField.value?.focus();
  } catch { if (!signal.aborted) { mode.value = 'text'; error.value = 'Mikrofon nebo přepis není dostupný. Pokračuj psaním; postup zůstává uložený.'; } }
  finally { if (!signal.aborted) { microphonePending.value = false; voicePhase.value = 'off'; } }
}
function useText() { stopMedia(); mode.value = 'text'; void nextTick(() => answerField.value?.focus()); }
async function saveCorrection() {
  const step = correction.value;
  if (!step?.answerId || !session.value || busy.value || !correctionText.value.trim()) return;
  const scope = controller;
  busy.value = true; error.value = ''; stopMedia();
  try {
    const value = await workshopSessionRequest(`${workshopSessionPath(session.value.id)}/answers/${step.answerId}`, 'PUT', { answerRevision: step.answerRevision, answer: correctionText.value.trim(), sourceMode: 'text' }, scope.signal);
    if (currentScope(scope)) { correction.value = null; applySession(value); }
  } catch { if (currentScope(scope)) { await refresh(); if (currentScope(scope)) error.value ||= 'Opravu se nepodařilo uložit. Zkontroluj obnovený přepis.'; } }
  finally { if (currentScope(scope)) busy.value = false; }
}
async function retryArtwork() {
  const current = session.value;
  const art = current?.artwork;
  if (busy.value || !current || !wand.value) return;
  const scope = controller;
  busy.value = true; artworkError.value = ''; loadedArtwork = '';
  try {
    if (art?.status === 'ready') await updateArtwork(current);
    else {
      artworkRetryId ||= secureUuid();
      const response = await accountTransport(`${workshopSessionPath(current.id)}/artwork/retry`, { method: 'POST', signal: scope.signal, headers: { 'Content-Type': 'application/json', 'Idempotency-Key': artworkRetryId }, body: '{}' });
      if (!currentScope(scope)) return;
      if (!response.ok) throw new Error('retry');
      artworkRetryId = '';
      await refreshSession();
    }
  } catch { if (currentScope(scope)) artworkError.value = 'Ilustraci se nepodařilo obnovit. Tvoje hůlka zůstává uložená.'; }
  finally { if (currentScope(scope)) busy.value = false; }
}
async function retryManufacture() {
  if (busy.value || !session.value?.canRetry) return;
  const scope = controller;
  busy.value = true; error.value = ''; stopMedia();
  try {
    const current = await loadWorkshopSession(session.value.id, scope.signal);
    if (!currentScope(scope)) return;
    applySession(current);
    if (current.status !== 'failed' || !current.canRetry) return;
    const value = await workshopSessionRequest(`${workshopSessionPath(current.id)}/retry`, 'POST', {}, scope.signal);
    if (currentScope(scope)) applySession(value);
  } catch { if (currentScope(scope)) { await refresh(); if (currentScope(scope)) error.value ||= 'Obnovení průběhu se nepodařilo potvrdit. Zkontroluj stav stejné operace.'; } }
  finally { if (currentScope(scope)) busy.value = false; }
}
function visibility() { if (document.hidden) stopMedia(); }
watch(() => [account.value?.id, props.upgrade], () => {
  controller.abort(); controller = new AbortController(); busy.value = false;
  stopMedia(); releaseArtwork(); session.value = null; entry.value = undefined; operation.value = ''; draft.value = ''; void refresh();
});
onMounted(() => { void refresh(); document.addEventListener('visibilitychange', visibility); });
onBeforeUnmount(() => {
  ++epoch; controller.abort(); mediaController.abort(); clearTimeout(poll); voice.dispose(); releaseArtwork(); document.removeEventListener('visibilitychange', visibility);
});
</script>

<template>
  <section class="trunk-workshop" aria-labelledby="workshop-title">
    <header class="trunk-workshop__header"><RouterLink to="/play" @click="stopMedia">← Zpět do hry</RouterLink><span>Wand Night Stand</span></header>
    <div class="trunk-workshop__identity"><p class="trunk-workshop__eyebrow">{{ upgrade ? 'Nová kapitola tvé hůlky' : 'Tvoje první hůlka' }}</p><h1 id="workshop-title">Kufrova dílna</h1><p v-if="entry?.profile"><strong>{{ entry.profile.name }}</strong><span v-if="entry.profile.universe"> · {{ entry.profile.universe }}</span><span v-if="entry.profile.version"> · {{ entry.profile.version }}</span></p></div>
    <div class="trunk-workshop__scene">
      <TrunkStage :stage="stage" :assistant-output-level="outputLevel" :wand-artwork-url="artworkUrl" :wand-name="wand?.name" :artwork-status="artworkState" @artwork-error="artworkError = 'Ilustraci se nepodařilo zobrazit.'" />
      <section class="trunk-workshop__conversation" aria-label="Rozhovor s magickým kufrem">
        <p v-if="loading" role="status">Ověřujeme tvoji postavu a dílnu…</p>
        <template v-else-if="!session">
          <p>Kufr už zná tvoji postavu. Tři nové otázky mu pomohou vytvořit jedinou osobní hůlku.</p><p v-if="unavailable" role="status">{{ unavailable }}</p>
          <RouterLink v-if="!entry?.profile" class="aw-button aw-button--primary" to="/me/character">Připravit postavu</RouterLink>
          <template v-else-if="canStart"><p>Můžeš psát nebo mluvit. Mikrofon se zapne až na tvoje přání.</p><AwButton variant="primary" :busy="busy" @click="start">Otevřít dílnu</AwButton></template>
          <RouterLink v-else-if="account?.openAiConsent !== true" to="/me/account">Zkontrolovat souhlas se zpracováním pomocí AI</RouterLink>
        </template>
        <template v-else-if="wand">
          <p class="trunk-workshop__eyebrow">Hůlka je uložená</p><p>{{ wand.material }} · {{ wand.core }}</p><p v-if="session.verdict" class="trunk-workshop__verdict">{{ session.verdict }}</p>
          <RouterLink class="aw-button aw-button--primary" to="/play">Pokračovat s hůlkou</RouterLink>
          <AwButton :disabled="busy" @click="speak">Přehrát verdikt kufru</AwButton>
          <p v-if="artworkError" role="status">{{ artworkError }}</p>
          <AwButton v-if="session.artwork?.canRetry || artworkError || !session.artwork" :busy="busy" @click="retryArtwork">{{ session.artwork?.status === 'ready' ? 'Načíst ilustraci znovu' : 'Zkusit ilustraci znovu' }}</AwButton>
          <details class="trunk-workshop__properties"><summary>Vlastnosti a kouzla hůlky</summary><GeneratedWand :wand="wand" mobile :show-artwork="false" /></details>
        </template>
        <template v-else-if="question">
          <p class="trunk-workshop__eyebrow">Otázka {{ question.number }} ze {{ question.total }}</p><progress :value="completedSteps.length" :max="question.total" :aria-label="`Dokončeno ${completedSteps.length} ze ${question.total} otázek`" />
          <h2 :key="question.id" ref="questionHeading" class="trunk-workshop__question" tabindex="-1">{{ question.text }}</h2><p v-if="unavailable" role="alert">{{ unavailable }}</p>
          <p v-if="question.usedFallback" role="status">Kufr používá náhradní otázku. Tvoje postava i dosavadní odpovědi zůstávají součástí výroby.</p>
          <div class="trunk-workshop__voice-controls"><AwButton v-if="voicePhase !== 'speaking'" :disabled="busy || !canAnswer || voicePhase === 'processing'" @click="speak">Přehrát otázku</AwButton><AwButton v-else @click="stopMedia">Zastavit přednes</AwButton><AwButton v-if="voicePhase === 'listening'" variant="primary" @click="voice.finishRecording()">Dokončit nahrávku</AwButton><AwButton v-else-if="mode === 'voice'" :disabled="!canAnswer || voicePhase === 'processing' || microphonePending" @click="record">Zapnout mikrofon a nahrát odpověď</AwButton></div>
          <div class="trunk-workshop__mode"><button v-if="mode === 'voice'" type="button" @click="useText">Odpovědět psaním</button><button v-else type="button" :disabled="!canAnswer" @click="mode = 'voice'">Odpovědět hlasem</button></div>
          <form @submit.prevent="submit()"><label for="trunk-answer">{{ sourceMode === 'voice' ? 'Přepis odpovědi · před odesláním jej můžeš upravit' : 'Tvoje odpověď' }}</label><textarea id="trunk-answer" ref="answerField" v-model="draft" rows="3" maxlength="2000" :disabled="busy || !canAnswer || voicePhase === 'listening' || voicePhase === 'processing'" @input="sourceMode = 'text'" /><AwButton type="submit" variant="primary" :disabled="!canAnswer || !draft.trim() || voicePhase !== 'off'" :busy="busy">{{ question.number === 3 ? 'Odeslat a vyrobit hůlku' : 'Odeslat odpověď' }}</AwButton></form>
          <button class="trunk-workshop__skip" type="button" :disabled="!canAnswer" @click="submit(true)">Přeskočit tuto otázku</button>
        </template>
        <template v-else-if="session.status === 'authoring'"><h2>Kufr připravuje otázku.</h2><p>Uložené odpovědi zůstávají zachované. Můžeš odejít a později pokračovat.</p></template>
        <template v-else-if="session.status === 'generating'"><h2>Teď pracuje kufr.</h2><p>Vychází z tvé postavy a všech tří odpovědí. Můžeš odejít a vrátit se ke stejné výrobě.</p></template>
        <template v-else><p role="status">{{ completedSteps.length < 3 ? 'Přípravu otázky se nepodařilo potvrdit. Uložené odpovědi zůstávají zachované.' : 'Výroba zatím není potvrzená. Obnov stav této operace; novou výrobu nezakládej.' }}</p><AwButton v-if="session.canRetry" :busy="busy" @click="retryManufacture">{{ completedSteps.length < 3 ? 'Obnovit otázku' : 'Obnovit tuto výrobu' }}</AwButton></template>
        <p class="trunk-workshop__status" role="status" aria-live="polite"><span :data-active="voicePhase === 'listening' || voicePhase === 'speaking'" aria-hidden="true"></span>{{ status }}</p><p v-if="error" role="alert">{{ error }}</p><AwButton v-if="error || session?.status === 'failed'" :disabled="busy || loading" @click="refresh">Obnovit stav</AwButton>
        <details v-if="completedSteps.length" class="trunk-workshop__transcript"><summary>Přepis rozhovoru · {{ completedSteps.length }} ze 3 odpovědí</summary><ol><li v-for="step in completedSteps" :key="step.questionId"><strong>Kufr · otázka {{ step.number }}</strong><p>{{ step.question }}</p><strong>Ty</strong><p>{{ step.skipped ? 'Otázka výslovně přeskočena.' : step.answer }}</p><button v-if="session?.status === 'asking' && step.answerId && !correction" type="button" @click="correction = step; correctionText = step.answer ?? ''">Opravit odpověď {{ step.number }}</button></li></ol><form v-if="correction" @submit.prevent="saveCorrection"><label for="trunk-correction">Oprava odpovědi {{ correction.number }}</label><textarea id="trunk-correction" v-model="correctionText" maxlength="2000" rows="3" /><AwButton type="submit" :disabled="busy || !correctionText.trim()">Uložit opravu</AwButton><AwButton @click="correction = null">Zrušit opravu</AwButton></form></details>
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
