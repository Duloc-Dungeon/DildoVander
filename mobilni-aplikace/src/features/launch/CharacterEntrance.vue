<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import ViewportCurtain from '../../components/scene/ViewportCurtain.vue';
import userIcon from '../../assets/wns-v2/icons/regular/user.svg';
import type { PrelaunchCharacterResponse } from './generatedPrelaunchCharacter';
import { characterDisplayName } from '../customer/characterLabel';
import { characterWelcomeText } from './characterWelcomeText';

const props = defineProps<{
  character: PrelaunchCharacterResponse['character'];
  artwork: PrelaunchCharacterResponse['artwork'];
  image: string;
  failed: boolean;
  readFailed: boolean;
  active: boolean;
  busy: boolean;
  retryAttempted?: boolean;
  retrying?: boolean;
}>();
const emit = defineEmits<{ continue: []; retry: []; failed: [] }>();
const elapsed = ref(0);
const preparationWaitMs = 5 * 60_000;
const canContinue = computed(() => !props.busy && (props.retryAttempted && !props.retrying
  || phase.value === 'portrait' && !!props.character || elapsed.value >= preparationWaitMs));
const revealed = ref(false);
const curtainDismissed = ref(false);
const reducedMotion = ref(false);
let motionQuery: MediaQueryList | undefined;
const heading = ref<HTMLElement>();
const personalSince = ref(0);
let lastTick = 0;
let timer: ReturnType<typeof setInterval> | undefined;
const personalJoke = computed(() => characterWelcomeText(props.artwork?.welcome?.joke) || characterWelcomeText(props.artwork?.loadingMessage));
const showPersonal = computed(() => !!personalJoke.value && elapsed.value >= 900);
const preparationFailed = computed(() => props.failed || props.readFailed
  || ['failed', 'not_requested', 'unavailable'].includes(props.artwork?.status ?? ''));
const unavailable = computed(() => preparationFailed.value || !props.image && elapsed.value >= preparationWaitMs);
const phase = computed(() => unavailable.value ? 'placeholder' : revealed.value && props.image ? 'portrait' : showPersonal.value ? 'personal' : 'waiting');
const showCurtain = computed(() => phase.value === 'portrait' && props.active && !reducedMotion.value && !curtainDismissed.value);
const title = computed(() => {
  if (phase.value === 'portrait' || phase.value === 'placeholder' && props.character) {
    return props.character ? characterDisplayName(props.character.name, props.character.label) : 'Tvoje postava';
  }
  return phase.value === 'placeholder' ? 'Podoba se nedostavila.' : 'Tohle je tvůj nástup.';
});
const status = computed(() => {
  if (props.busy) return 'Ověřujeme vstup do večera…';
  if (props.retrying && elapsed.value < preparationWaitMs) return 'Zkoušíme znovu načíst postavu a portrét…';
  if (phase.value === 'portrait' && props.character) return 'Postava je připravená. Večer může začít.';
  if (props.retryAttempted && !props.retrying) return 'Pokus o načtení je dokončený. Můžeš pokračovat i bez dokončené přípravy.';
  if (elapsed.value >= preparationWaitMs) return 'Příprava zatím není dokončená. Můžeš pokračovat a vrátit se k ní později.';
  if (preparationFailed.value) return 'Načtení se nepodařilo. Můžeš ho jednou zkusit znovu.';
  return 'Čekáme na postavu a portrét, nejvýše pět minut. Jakmile budou připravené, můžeš pokračovat.';
});

function tick(): void {
  const now = performance.now();
  if (props.active) elapsed.value += Math.max(0, now - lastTick);
  lastTick = now;
}
function motion(event: MediaQueryListEvent): void {
  reducedMotion.value = event.matches;
  if (event.matches) curtainDismissed.value = true;
}
watch(() => props.active, active => {
  lastTick = performance.now();
  if (!active && phase.value === 'portrait') curtainDismissed.value = true;
});
watch(personalJoke, () => { personalSince.value = elapsed.value; });
watch(() => props.image, () => { revealed.value = false; curtainDismissed.value = false; }, { flush: 'sync' });
watch([elapsed, () => props.image, () => props.active], () => {
  const introductionEnds = personalJoke.value ? Math.max(900, personalSince.value) + 1800 : 1600;
  if (!revealed.value && props.active && props.image && elapsed.value >= introductionEnds) {
    revealed.value = true;
    curtainDismissed.value = reducedMotion.value;
  }
}, { flush: 'sync' });
onMounted(() => {
  heading.value?.focus({ preventScroll: true });
  if (typeof window.matchMedia === 'function') {
    motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
    reducedMotion.value = motionQuery.matches;
    motionQuery.addEventListener('change', motion);
  }
  lastTick = performance.now();
  timer = setInterval(tick, 100);
});
onBeforeUnmount(() => { clearInterval(timer); motionQuery?.removeEventListener('change', motion); });
</script>

<template>
  <section class="character-entrance" :class="{ 'entrance-paused': !active, 'entrance-open': phase === 'portrait' || phase === 'placeholder' }" :data-entrance="phase" aria-labelledby="entrance-title">
    <div class="entrance-stage">
      <div class="entrance-proscenium">
        <div class="entrance-placeholder" :aria-hidden="phase !== 'placeholder'">
          <img :src="userIcon" alt="" width="112" height="112" />
          <span>Podoba neznámá.<br />Pověst předchází.</span>
        </div>
        <img v-if="image" class="entrance-portrait" :class="{ 'portrait-visible': phase === 'portrait' }" :src="image" :alt="`Portrét postavy ${character?.name ?? ''}`" :aria-hidden="phase !== 'portrait'" @error="emit('failed')" />
        <div class="entrance-seal" aria-hidden="true"><span>W</span></div>
      </div>
      <p class="entrance-caption">{{ phase === 'portrait' ? character?.universe : 'Wand Night Stand' }}</p>
    </div>
    <div class="entrance-copy">
      <h1 id="entrance-title" ref="heading" tabindex="-1">{{ title }}</h1>
      <Transition name="entrance-line" mode="out-in">
        <p :key="showPersonal ? personalJoke : 'waiting'" class="entrance-joke">{{ showPersonal ? personalJoke : 'Slavnostní příchod je připravený. Slavná osoba se ještě hledá v seznamu hostů.' }}</p>
      </Transition>
      <div v-if="phase === 'portrait' || phase === 'placeholder'" class="entrance-identity">
        <p v-if="artwork?.welcome?.identity">{{ artwork.welcome.identity }}</p>
        <p v-if="artwork?.welcome?.teaser" class="entrance-teaser">{{ artwork.welcome.teaser }}</p>
      </div>
      <p v-if="phase === 'placeholder'" class="entrance-missing-joke">Portrét jsme nenašli. Malíř prý zachytil tvou podstatu, ale zapomněl ji odevzdat.</p>
      <slot />
      <p class="entrance-status" role="status">{{ status }}</p>
      <AwButton v-if="canContinue" class="entrance-continue" variant="primary" :busy="busy" @click="emit('continue')">Pokračovat</AwButton>
      <button v-if="phase === 'placeholder' && !retryAttempted" class="entrance-retry" :disabled="busy || retrying" @click="emit('retry')">Zkusit načíst znovu</button>
    </div>
    <ViewportCurtain v-if="showCurtain" :duration-ms="1_200" :active="active" @finished="curtainDismissed = true" />
  </section>
</template>

<style scoped>
.character-entrance { width: min(100%, 70rem); margin: auto; padding: max(24px, env(safe-area-inset-top)) 24px max(32px, env(safe-area-inset-bottom)); display: grid; grid-template-columns: minmax(0, 1fr); gap: 28px; color: var(--wns-text); }
.entrance-stage { width: min(100%, 340px); margin-inline: auto; min-width: 0; }
.entrance-proscenium { position: relative; isolation: isolate; aspect-ratio: 4 / 5; overflow: hidden; border-radius: 160px 160px 18px 18px; background: var(--wns-peach); outline: 1px solid var(--wns-amber); outline-offset: 7px; }
.entrance-placeholder { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 24px; padding: 28px; color: var(--wns-on-peach); text-align: center; }
.entrance-placeholder img { opacity: .65; }
.entrance-placeholder span { font-size: 1rem; font-weight: 650; line-height: 1.5; }
.entrance-portrait { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; object-position: center 25%; opacity: 0; transform: scale(1.06); transition: opacity .65s ease-out, transform 1.2s cubic-bezier(.16, 1, .3, 1); }
.portrait-visible { opacity: 1; transform: scale(1); }
.entrance-seal { position: absolute; inset: 0; display: grid; place-items: center; pointer-events: none; opacity: 1; transition: opacity .25s ease-out, transform .5s ease-out; }
.entrance-seal span { display: grid; place-items: center; width: 80px; height: 80px; border-radius: 50%; background: var(--wns-amber); color: var(--wns-on-amber); font-size: 2.5rem; font-weight: 800; transform: rotate(-8deg); }
.entrance-open .entrance-seal { opacity: 0; transform: scale(1.15); }
.entrance-caption { margin: 20px 0 0; text-align: center; color: var(--wns-muted); font-size: .8rem; }
.entrance-copy { align-self: center; min-width: 0; }
.character-entrance h1 { margin: 0 0 20px; max-width: 14ch; font-size: clamp(2.2rem, 8vw, 4rem); font-weight: 800; letter-spacing: -.035em; line-height: 1.06; text-wrap: balance; overflow-wrap: anywhere; }
.character-entrance h1:focus { outline: none; }
.entrance-joke { margin: 0 0 24px; max-width: 34ch; color: var(--wns-text); font-size: clamp(1.1rem, 3.8vw, 1.4rem); line-height: 1.55; }
.entrance-identity { margin-block: 24px; animation: entrance-name .7s cubic-bezier(.16, 1, .3, 1) both; }
.entrance-identity p { margin: 0 0 12px; }
.entrance-teaser { color: var(--wns-amber); }
.entrance-missing-joke { max-width: 38ch; margin-block: 20px; color: var(--wns-peach); }
.entrance-status { max-width: 40ch; margin: 28px 0 16px; color: var(--wns-muted); font-size: .875rem; }
.character-entrance :deep(.entrance-continue) { width: 100%; min-height: 56px; }
.entrance-retry { display: block; margin: 12px auto 0; padding: 10px; min-height: 44px; background: transparent; border: 0; color: var(--wns-text); text-decoration: underline; text-underline-offset: .2em; cursor: pointer; }
.entrance-retry:hover { color: var(--wns-mint); }
.entrance-retry:disabled { opacity: .5; cursor: wait; }
.character-entrance :is(a, button):focus-visible { outline: 3px solid var(--wns-mint); outline-offset: 4px; }
.character-entrance ::selection { background: var(--wns-violet); color: var(--wns-on-violet); }
.entrance-line-enter-active, .entrance-line-leave-active { transition: opacity .22s ease-out, transform .22s ease-out; }
.entrance-line-enter-from { opacity: 0; transform: translateY(8px); }
.entrance-line-leave-to { opacity: 0; transform: translateY(-8px); }
.entrance-paused .entrance-identity { animation-play-state: paused; }
@keyframes entrance-name { from { opacity: .3; transform: translateY(12px); } to { opacity: 1; transform: translateY(0); } }
@media (min-width: 760px) { .character-entrance { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: clamp(40px, 6vw, 90px); padding: 64px 40px; } .entrance-stage { width: min(100%, 440px); } }
@media (max-width: 759px) and (min-height: 700px) { .entrance-stage { width: min(100%, 260px); } }
@media (prefers-reduced-motion: reduce) { .entrance-portrait, .entrance-seal, .entrance-line-enter-active, .entrance-line-leave-active { transition: none; } .entrance-identity { animation: none; } }
</style>
