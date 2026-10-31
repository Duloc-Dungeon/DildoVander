<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { onBeforeRouteLeave } from 'vue-router';
import SoundToggle from '../../application/SoundToggle.vue';
import { account } from '../identity/session';
import storeroomUrl from '../../assets/arcade/storeroom.webp';
import sparkUrl from '../../assets/arcade/spark.webp';
import { createCourse, createSpark, FIELD, MAX_GATES, MAX_TAPS, PRACTICE_SEED, RULES_VERSION, stepSpark, TICK_MS } from './sparkEngine';
import { createSparkRenderer, type SparkRenderer } from './sparkRenderer';
import { createSparkAudio } from './sparkAudio';
import { gatesLabel, progressionHint, resultCopy } from './sparkCopy';
import type { SparkTicket } from './sparkApi';
import { useSparkCompetition } from './useSparkCompetition';
import './spark.css';

const competition = useSparkCompetition();
const { ticket, preparing, error, saving, pending, receipt, best, next } = competition;
const phase = ref<'ready' | 'playing' | 'paused' | 'result'>('ready');
const score = ref(0);
const host = ref<HTMLElement>();
const surface = ref<HTMLElement>();
const resultHeading = ref<HTMLElement>();
const graphicsReady = ref(false);
const graphicsError = ref('');
const practiceBest = ref(0);
const runIsRanked = ref(false);
const resultState = ref(createSpark());
const resultRunId = ref('');
const practiceRecord = ref(false);
const haptics = ref(false);
const restartReady = ref(false);
const pauseReason = ref('Pokus je pozastavený.');
const reduced = ref(false);
const audio = createSparkAudio();
const practiceKey = `wns.spark.practice.${RULES_VERSION}.${account.value?.id ?? 'anonymous'}`;
let renderer: SparkRenderer | undefined;
let state = createSpark();
let course = createCourse(PRACTICE_SEED);
let issued: SparkTicket | undefined;
let taps: number[] = [];
let pendingTaps = 0;
let frame = 0;
let lastTime = 0;
let accumulator = 0;
let restartTimer = 0;
let activePointer: number | undefined;
let disposed = false;
let media: MediaQueryList | undefined;

const verified = computed(() => receipt.value?.runId === resultRunId.value ? receipt.value.value : undefined);
const result = computed(() => resultCopy(resultState.value, runIsRanked.value ? Boolean(verified.value?.newBest) : practiceRecord.value));
const target = computed(() => best.value > 0 ? `Tvůj rekord ${best.value}` : next.value ? `${next.value.displayName}: ${next.value.score}` : 'Každá brána = 1 bod');
const lesson = computed(() => progressionHint(score.value));
const resultSaved = computed(() => !runIsRanked.value ? 'Trénink se do skupinového žebříčku nezapisuje.'
  : verified.value ? 'Výsledek ověřený a zapsaný v žebříčku.'
    : pending.value.some(run => run.ticket.runId === resultRunId.value) ? 'Výsledek čeká na ověření. Můžeš hrát dál.' : 'Výsledek se do žebříčku nezapsal.');

function render() { renderer?.draw(state, course, reduced.value); }
function stopFrame() { cancelAnimationFrame(frame); frame = 0; }
function pause(reason = 'Pokus je pozastavený.') {
  if (phase.value !== 'playing') return;
  phase.value = 'paused'; pauseReason.value = reason;
  stopFrame(); accumulator = 0; activePointer = undefined;
}
function finish() {
  stopFrame(); phase.value = 'result'; score.value = state.score;
  resultState.value = { ...state }; resultRunId.value = issued?.runId ?? '';
  practiceRecord.value = !issued && state.score > practiceBest.value;
  if (!issued && state.score > practiceBest.value) {
    practiceBest.value = state.score;
    try { localStorage.setItem(practiceKey, String(state.score)); } catch { /* Best remains in this session. */ }
  }
  if (issued) competition.submit(issued, { endTick: state.tick, taps: [...taps] });
  audio.play('crash');
  if (haptics.value) navigator.vibrate?.(35);
  // A pre-collision pointer/click never becomes the next start gesture.
  restartReady.value = false;
  clearTimeout(restartTimer);
  restartTimer = window.setTimeout(() => { restartReady.value = true; void nextTick(() => resultHeading.value?.focus()); }, 360);
  render();
}
function advance(now: number) {
  if (phase.value !== 'playing') return;
  const elapsed = now - lastTime;
  lastTime = now;
  if (elapsed > 250) { pause('Aplikace se na chvíli zastavila. Pokračuj, až budeš připravený.'); return; }
  accumulator += Math.max(0, elapsed);
  while (accumulator >= TICK_MS && phase.value === 'playing') {
    const previousScore = state.score;
    stepSpark(state, course, pendingTaps); pendingTaps = 0;
    accumulator -= TICK_MS;
    if (state.score !== previousScore) {
      score.value = state.score;
      audio.play(state.score === (runIsRanked.value ? best.value : practiceBest.value) + 1 ? 'record' : 'gate');
      if (haptics.value) navigator.vibrate?.(9);
    }
    if (state.status !== 'flying') finish();
  }
}
function loop(now: number) {
  advance(now); render();
  if (phase.value === 'playing') frame = requestAnimationFrame(loop);
}
function fly() {
  phase.value = 'playing'; lastTime = performance.now(); accumulator = 0;
  audio.unlock(); surface.value?.focus({ preventScroll: true });
  frame = requestAnimationFrame(loop);
}
function start(ranked: boolean) {
  if (!graphicsReady.value || phase.value === 'playing' || (phase.value === 'result' && !restartReady.value)) return;
  if (activePointer !== undefined) return;
  issued = ranked ? competition.takeTicket() : undefined;
  if (ranked && !issued) return;
  runIsRanked.value = Boolean(issued);
  state = createSpark(); course = createCourse(issued?.seed ?? PRACTICE_SEED);
  taps = []; pendingTaps = 0; score.value = 0; resultRunId.value = ''; practiceRecord.value = false;
  render(); fly();
}
function turn() {
  if (phase.value !== 'playing') return;
  advance(performance.now());
  if (phase.value !== 'playing') return;
  if (taps.length >= MAX_TAPS) { pause('Pokus dosáhl limitu ovládacích vstupů. Vrať se ke hrám a začni nový.'); return; }
  taps.push(state.tick); pendingTaps++;
  audio.unlock(); audio.play('turn');
}
function pointerDown(event: PointerEvent) {
  if (!event.isPrimary || event.button !== 0 || activePointer !== undefined || phase.value !== 'playing') return;
  event.preventDefault(); activePointer = event.pointerId;
  surface.value?.setPointerCapture?.(event.pointerId);
  turn();
}
function pointerUp(event: PointerEvent) { if (activePointer === event.pointerId) activePointer = undefined; }
function pointerCancel(event: PointerEvent) { pointerUp(event); pause('Dotyk se přerušil. Pokračuj, až budeš připravený.'); }
function keyDown(event: KeyboardEvent) {
  if ((event.code === 'Space' || event.code === 'Enter') && !event.repeat && phase.value === 'playing') {
    event.preventDefault(); turn();
  } else if (event.code === 'Escape' && phase.value === 'playing') { event.preventDefault(); pause(); }
}
function visibility() { if (document.hidden) pause('Aplikace byla skrytá. Pokus na tebe počkal.'); }
function blur() { pause('Pokus na tebe počká.'); }
function motionChanged() { reduced.value = media?.matches ?? false; render(); }
async function setupGraphics() {
  if (!host.value) return;
  graphicsError.value = ''; graphicsReady.value = false;
  renderer?.destroy(); renderer = undefined;
  try {
    const created = await createSparkRenderer(host.value, () => {
      pause('Grafika se přerušila. Obnov ji a potom pokračuj.');
      graphicsReady.value = false; graphicsError.value = 'Grafiku je potřeba obnovit.';
    });
    if (disposed) { created.destroy(); return; }
    renderer = created; graphicsReady.value = true; render();
  } catch (problem) {
    console.warn('Spark renderer initialization failed', problem);
    if (!disposed) graphicsError.value = 'Herní grafiku se nepodařilo spustit. Zkus ji obnovit.';
  }
}
onMounted(() => {
  try { practiceBest.value = Math.max(0, Math.min(MAX_GATES, Number(localStorage.getItem(practiceKey)) || 0)); } catch { /* Optional. */ }
  media = window.matchMedia('(prefers-reduced-motion: reduce)'); motionChanged(); media.addEventListener('change', motionChanged);
  document.addEventListener('visibilitychange', visibility); window.addEventListener('blur', blur);
  void setupGraphics(); void competition.connect();
});
onBeforeRouteLeave(() => { pause(); });
onBeforeUnmount(() => {
  disposed = true; stopFrame(); clearTimeout(restartTimer); renderer?.destroy(); audio.destroy();
  document.removeEventListener('visibilitychange', visibility); window.removeEventListener('blur', blur); media?.removeEventListener('change', motionChanged);
});
</script>

<template>
  <section class="spark-page" aria-labelledby="spark-title">
    <header class="spark-topbar">
      <RouterLink to="/play" class="spark-back" aria-label="Zpět ke hrám"><svg aria-hidden="true" viewBox="0 0 20 20"><path d="M12.5 4.5 7 10l5.5 5.5M7.5 10H17" /></svg><span>Hry</span></RouterLink>
      <h1 id="spark-title">Kouzlo bez dozoru</h1>
      <SoundToggle />
    </header>
    <div class="spark-layout">
      <div class="spark-cabinet" :style="{ '--spark-art': `url(${storeroomUrl})` }" :data-phase="phase">
        <div ref="host" class="spark-renderer" />
        <div class="spark-hud" aria-hidden="true"><span>{{ phase === 'ready' ? 'Kouzelnický sklad' : runIsRanked ? 'Soutěžní pokus' : 'Trénink' }}</span><strong>{{ score }}</strong><small>{{ target }}</small></div>
        <div ref="surface" class="spark-touch" role="button" tabindex="0" aria-label="Obrátit směr jiskry. Mezerník nebo Enter obrací směr, Escape pozastaví hru." :aria-disabled="phase !== 'playing'"
          @pointerdown="pointerDown" @pointerup="pointerUp" @pointercancel="pointerCancel" @keydown="keyDown" />
        <button v-if="phase === 'playing'" class="spark-pause" aria-label="Pozastavit hru" @click.stop="pause()"><svg aria-hidden="true" viewBox="0 0 20 20"><path d="M6.5 4.5v11M13.5 4.5v11" /></svg></button>
        <div v-if="phase === 'ready'" class="spark-panel spark-intro">
          <img :src="sparkUrl" alt="" width="76" height="76" />
          <h2>První kouzlo se povedlo.<br /><span>Bohužel pokračuje.</span></h2>
          <p>Klepnutím obrať směr jiskry. Proleť otvory a ničeho se nedotkni.</p>
          <button class="spark-primary" :disabled="!ticket || !graphicsReady" @click="start(true)">{{ preparing ? 'Připravuji soutěžní pokus…' : ticket ? 'Hrát o rekord' : 'Soutěžní pokus není připravený' }}</button>
          <button class="spark-secondary" :disabled="!graphicsReady" @click="start(false)">Trénovat hned</button>
          <small>Jeden prst · jedna brána = jeden bod</small>
        </div>
        <div v-else-if="phase === 'paused'" class="spark-panel spark-paused">
          <h2>Pokus počká.</h2><p>{{ pauseReason }}</p>
          <button class="spark-primary" :disabled="!graphicsReady" @click="fly">Pokračovat</button>
          <RouterLink class="spark-secondary" to="/play">Zpět ke hrám</RouterLink>
        </div>
        <div v-else-if="phase === 'result' && restartReady" class="spark-panel spark-result">
          <h2 ref="resultHeading" tabindex="-1">{{ gatesLabel(score) }}</h2>
          <small>{{ runIsRanked ? 'Soutěžní výsledek' : 'Tréninkový výsledek' }}</small>
          <p class="spark-result-title">{{ result.title }}</p>
          <p class="spark-joke">{{ result.joke }}</p>
          <p class="spark-tip">{{ result.tip }}</p>
          <button class="spark-primary" :disabled="!graphicsReady || (runIsRanked && !ticket)" @click="start(runIsRanked)">{{ runIsRanked && !ticket ? 'Připravuji další pokus…' : 'Ještě jeden pokus' }}</button>
          <button v-if="runIsRanked" class="spark-secondary" :disabled="!graphicsReady" @click="start(false)">Trénovat hned</button>
          <small v-else>Tréninkový rekord: {{ practiceBest }}</small>
        </div>
        <p v-if="phase === 'playing'" class="spark-touch-hint" aria-hidden="true">{{ lesson }}<br />Klepnutím obrať směr ↔</p>
      </div>
      <aside class="spark-companion" aria-label="Výsledky a nastavení">
        <p class="spark-score-sr" role="status">{{ phase === 'paused' ? 'Hra pozastavena.' : phase === 'result' ? `${gatesLabel(score)}. ${resultSaved}` : phase === 'playing' ? `Hra běží. ${lesson}` : 'Hra připravena.' }}</p>
        <div class="spark-records"><span>Tvůj rekord ve skupině<strong>{{ best }}</strong></span><RouterLink to="/play/ranking#spark">Žebříček <svg aria-hidden="true" viewBox="0 0 16 16"><path d="M4 12 12 4M5 4h7v7" /></svg></RouterLink></div>
        <p v-if="next" class="spark-next">Další cíl: {{ next.displayName }} · {{ gatesLabel(next.score) }}</p>
        <p v-if="phase === 'result'" class="spark-delivery" role="status">{{ resultSaved }}</p>
        <div v-if="graphicsError" class="spark-message" role="alert"><p>{{ graphicsError }}</p><button class="spark-secondary" @click="setupGraphics">Obnovit grafiku</button></div>
        <div v-if="error || pending.length" class="spark-message" role="status"><p>{{ error || `${pending.length} výsledků čeká na ověření.` }}</p>
          <button v-if="pending.length" class="spark-secondary" :disabled="saving" @click="competition.retry">{{ saving ? 'Ověřuji…' : 'Odeslat výsledky znovu' }}</button>
          <button v-if="!ticket && !preparing" class="spark-secondary" @click="competition.connect">Obnovit spojení</button>
        </div>
        <details class="spark-guide"><summary>Jak hrát a co se počítá</summary>
          <p>Jiskra letí sama. Každé klepnutí obrátí její boční směr. Nejdřív tref otvor, pak si připrav nájezd do dalšího. Světlé jádro je přesná velikost jiskry pro náraz; záře kolem se nepočítá.</p>
          <p>Noční inventura: regály se daly do úklidu. Začaly tebou.</p>
          <ul>
            <li>Schody, esíčko a rukáv tvoří krátké sestavy. Sleduj výjezd i další dva otvory.</li>
            <li>Od 15 bodů se některé otvory pohybují. Kolejnice ukazují celý rozkmit a šipka směr. Později se přidají společné i protiběžné dvojice a dlouhé pohyblivé průlety.</li>
            <li>Od 20 bodů potkáš plný kosočtverec. Otiskne polohu jiskry do pečeti o dvě brány dál. Pečeť uvidíš hned a její poloha už zůstane stejná. Oblétni ji.</li>
            <li>Od 100 bodů mohou navazovat dvě ozvěny. Mezi sestavami zůstávají širší klidné průlety.</li>
          </ul>
          <p>Do skupinového žebříčku jde nejlepší ověřený pokus, nikoli součet. Ve skupině máte stejný základ tratě a fyziku; polohu pečetí určuje tvůj průlet. Noční inventura má vlastní rekordy. Po skrytí aplikace se čas zastaví. Trénink má vlastní rekord a do pořadí se nezapisuje.</p>
          <p>Na klávesnici použij mezerník nebo Enter; Escape hru pozastaví. Směr ukazuje malá šipka u jiskry.</p>
          <label><input v-model="haptics" type="checkbox" /> Krátké vibrace, pokud je telefon podporuje</label>
        </details>
      </aside>
    </div>
  </section>
</template>
