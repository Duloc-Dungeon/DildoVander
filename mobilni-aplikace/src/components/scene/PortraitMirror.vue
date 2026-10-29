<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useAnimate } from 'motion-v';

const props = withDefaults(defineProps<{
  open: boolean;
  active?: boolean;
  animateReveal?: boolean;
  replayable?: boolean;
  preparing?: boolean;
}>(), {
  active: true,
  animateReveal: true,
  replayable: true,
  preparing: false,
});
const emit = defineEmits<{ replay: [] }>();
const [scope, animate] = useAnimate();
const state = ref<'closed' | 'revealing' | 'open'>('closed');
const reduced = ref(false);
const waitingVisible = ref(true);
let query: MediaQueryList | undefined;
let waitingObserver: IntersectionObserver | undefined;
let playback: ReturnType<typeof animate> | undefined;
const replaying = ref(false);

function update(): void {
  if (!scope.value) return;
  if (!props.replayable) replaying.value = false;
  if (!props.open) {
    playback?.complete();
    playback = undefined;
    replaying.value = false;
    state.value = 'closed';
    return;
  }
  if (reduced.value || !props.animateReveal && !replaying.value) {
    playback?.complete();
    playback = undefined;
    replaying.value = false;
    state.value = 'open';
    return;
  }
  if (state.value === 'open') return;
  if (!playback) {
    state.value = 'revealing';
    playback = animate([
      ['.mirror-mist', { '--mirror-opening': ['-22%', '145%'], opacity: [1, 1, 0] },
        { at: .08, duration: 1.02, times: [0, .82, 1], ease: [.22, 1, .36, 1] }],
      ['.mirror-glint', { x: ['-160%', '480%'], rotate: -18, opacity: [0, .6, 0] },
        { at: .45, duration: .65, ease: [.16, 1, .3, 1] }],
      ['.mirror-mark', { opacity: [1, 0], scale: [1, .94] }, { at: 0, duration: .22 }],
    ]);
    const current = playback;
    void current.then(() => {
      if (playback === current && props.open) {
        state.value = 'open';
        replaying.value = false;
      }
    });
  }
  if (props.active) playback.play();
  else playback.pause();
}
function replay(): void {
  if (!props.open || !props.active || !props.replayable || reduced.value || state.value !== 'open') return;
  scope.value?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  playback?.complete();
  playback = undefined;
  replaying.value = true;
  state.value = 'closed';
  update();
  emit('replay');
}
function motion(event: MediaQueryListEvent): void { reduced.value = event.matches; }
function observeWaiting(): void {
  waitingObserver?.disconnect();
  waitingObserver = undefined;
  waitingVisible.value = true;
  if (!scope.value || !props.preparing || props.open) return;
  waitingObserver = new IntersectionObserver(entries => {
    waitingVisible.value = !!entries[0]?.isIntersecting;
  });
  waitingObserver.observe(scope.value);
}
watch([() => props.open, () => props.active, () => props.animateReveal, () => props.replayable, reduced], update, { flush: 'post' });
watch([() => props.preparing, () => props.open], observeWaiting, { flush: 'post' });
onMounted(() => {
  query = window.matchMedia('(prefers-reduced-motion: reduce)');
  reduced.value = query.matches;
  query.addEventListener('change', motion);
  observeWaiting();
  update();
});
onBeforeUnmount(() => {
  query?.removeEventListener('change', motion);
  waitingObserver?.disconnect();
  playback?.complete();
  playback = undefined;
});
</script>

<template>
  <div ref="scope" class="portrait-mirror" :class="{ 'mirror-inactive': !active || !waitingVisible }" :data-mirror="state">
    <slot />
    <div class="mirror-glass" aria-hidden="true">
      <div class="mirror-mist"><div v-if="preparing && !open" class="mirror-waiting-glow" /></div>
      <div class="mirror-glint" />
      <div class="mirror-mark">
        <svg viewBox="0 0 64 80" fill="none"><path d="M32 3C14 3 5 18 5 37v36h54V37C59 18 50 3 32 3Z" /><path d="m21 44 11-17 11 17-11 17-11-17Zm11-17v34M17 67h30" /></svg>
      </div>
    </div>
    <button v-if="open && replayable && !reduced && (state === 'open' || replaying)" class="mirror-replay" type="button"
      :aria-disabled="state !== 'open' || !active"
      :aria-label="state === 'revealing' ? 'Odhalení se přehrává' : 'Přehrát odhalení znovu'"
      title="Přehrát odhalení znovu" @click="replay">
      <svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M4 10a8 8 0 1 1 .7 6M4 4v6h6" /></svg>
    </button>
  </div>
</template>

<style scoped>
.portrait-mirror { position: relative; isolation: isolate; overflow: hidden; aspect-ratio: 4 / 5; border-radius: 48% 48% 20px 20px; background: var(--wns-peach); outline: 1px solid var(--wns-amber); outline-offset: 7px; }
.mirror-glass { position: absolute; inset: 0; overflow: hidden; border-radius: inherit; pointer-events: none; }
.mirror-glass::before { content: ''; position: absolute; inset: 0; background: linear-gradient(118deg, color-mix(in srgb, var(--wns-text) 9%, transparent), transparent 35% 76%, color-mix(in srgb, var(--wns-text) 5%, transparent)); }
.mirror-glass::after { content: ''; position: absolute; inset: 8px; border: 1px solid color-mix(in srgb, var(--wns-amber) 38%, transparent); border-radius: inherit; }
.mirror-mist { --mirror-opening: -22%; position: absolute; inset: 0; background: radial-gradient(ellipse at 32% 18%, color-mix(in srgb, var(--wns-text) 60%, var(--wns-violet)), transparent 64%), linear-gradient(145deg, var(--wns-violet), color-mix(in srgb, var(--wns-violet) 65%, var(--wns-mint)) 65%, color-mix(in srgb, var(--wns-violet) 70%, var(--wns-on-violet))); }
.mirror-mist::after { content: ''; position: absolute; inset: 0; opacity: .45; background: radial-gradient(2px 4px at 18% 34%, var(--wns-text) 20%, transparent 80%), radial-gradient(1px 3px at 23% 52%, var(--wns-text) 20%, transparent 80%), radial-gradient(2px 3px at 78% 30%, var(--wns-text) 20%, transparent 80%), radial-gradient(1px 4px at 82% 61%, var(--wns-text) 20%, transparent 80%), radial-gradient(2px 4px at 70% 78%, var(--wns-text) 20%, transparent 80%); }
.mirror-waiting-glow { position: absolute; inset: -25%; background: radial-gradient(ellipse at 35% 38%, color-mix(in srgb, var(--wns-text) 70%, transparent), transparent 48%), radial-gradient(ellipse at 70% 65%, var(--wns-mint), transparent 45%); animation: mirror-waiting-breath 4.6s ease-in-out infinite alternate; }
.mirror-inactive .mirror-waiting-glow { animation-play-state: paused; }
@keyframes mirror-waiting-breath { from { opacity: .3; transform: translate(-3%, 3%) scale(.94); } to { opacity: .8; transform: translate(3%, -3%) scale(1.04); } }
@supports (mask-image: radial-gradient(transparent, black)) {
  .mirror-mist { mask-image: radial-gradient(ellipse at 50% 43%, transparent var(--mirror-opening), black calc(var(--mirror-opening) + 22%)); }
}
.mirror-glint { position: absolute; inset: -30% auto; width: 32%; height: 160%; opacity: 0; transform: translateX(-160%) rotate(-18deg); background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--wns-text) 8%, transparent) 36%, color-mix(in srgb, var(--wns-text) 35%, transparent) 50%, color-mix(in srgb, var(--wns-text) 8%, transparent) 64%, transparent); }
.mirror-mark { position: absolute; inset: 0; display: grid; place-items: center; color: var(--wns-on-violet); }
.mirror-mark svg { width: 56px; height: 70px; stroke: currentColor; stroke-width: 1.2; }
.mirror-replay { position: absolute; inset: auto 12px 12px auto; width: 44px; height: 44px; display: grid; place-items: center; padding: 0; border: 0; border-radius: 50%; background: var(--wns-surface); color: var(--wns-mint); cursor: pointer; transition: background-color .16s ease-out, color .16s ease-out; }
.mirror-replay svg { width: 20px; height: 20px; stroke: currentColor; stroke-width: 1.5; stroke-linecap: round; stroke-linejoin: round; }
.mirror-replay:hover { background: var(--wns-mint); color: var(--wns-on-mint); }
.mirror-replay:active { background: var(--wns-amber); color: var(--wns-on-amber); }
.mirror-replay:focus-visible { outline: 3px solid var(--wns-mint); outline-offset: 3px; box-shadow: 0 0 0 6px var(--wns-bg); }
.mirror-replay[aria-disabled='true'] { cursor: default; color: var(--wns-muted); background: var(--wns-surface); }
[data-mirror='open'] .mirror-mist, [data-mirror='open'] .mirror-glint, [data-mirror='open'] .mirror-mark { display: none; }
[data-mirror='closed'] .mirror-mist { opacity: 1 !important; --mirror-opening: -22% !important; }
[data-mirror='closed'] .mirror-glint { opacity: 0 !important; }
[data-mirror='closed'] .mirror-mark { opacity: 1 !important; transform: none !important; }
@media (prefers-reduced-motion: reduce), (update: slow) {
  [data-mirror='revealing'] .mirror-mist, [data-mirror='revealing'] .mirror-glint, [data-mirror='revealing'] .mirror-mark { display: none; }
  .mirror-replay { display: none; }
  .mirror-waiting-glow { animation: none; opacity: .35; transform: none; }
}
</style>
