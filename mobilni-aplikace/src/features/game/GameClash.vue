<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import GameSpellToken from './GameSpellToken.vue';
import { actionName, type Choice, type Round } from './game';

const props = defineProps<{ round?: Round; locked?: boolean; wandUrl?: string; wandName?: string; synthetic?: boolean; opponentWandUrl?: string; opponentWandName?: string; opponentSynthetic?: boolean }>();
const stage = ref<HTMLElement>();
const canvas = ref<HTMLCanvasElement>();
const playerOrigin = ref<HTMLElement>();
const opponentOrigin = ref<HTMLElement>();
const artworkFailed = ref([false, false]);
const motion = ref<'still' | 'playing'>('still');
const revealedActions = computed(() => {
  const choices = [props.round?.playerChoice, props.round?.opponentChoice];
  return choices.every(choice => choice === 'strike' || choice === 'trick' || choice === 'shield')
    ? choices as Choice[] : [];
});
const tips = [{ x: .5, y: .05 }, { x: .5, y: .05 }];
let frame = 0;
let disposed = false;
let inView = false;
let pendingReveal = false;
let observer: IntersectionObserver | undefined;
let resizeObserver: ResizeObserver | undefined;
let reducedMotion: MediaQueryList | undefined;

watch(() => props.wandUrl, () => { artworkFailed.value[0] = false; tips[0] = { x: .5, y: .05 }; });
watch(() => props.opponentWandUrl, () => { artworkFailed.value[1] = false; tips[1] = { x: .5, y: .05 }; });

// Backend PNGs are upright 2:3 objects with variable transparent margins.
// Sample the solid tip once per image; preserve the complete original artwork.
function artworkLoaded(event: Event, side: number) {
  const image = event.target as HTMLImageElement;
  try {
    const sample = document.createElement('canvas');
    sample.width = 96; sample.height = 144;
    const context = sample.getContext('2d', { willReadFrequently: true });
    if (!context) return;
    context.drawImage(image, 0, 0, 96, 144);
    const pixels = context.getImageData(0, 0, 96, 144).data;
    for (let y = 0; y < 144; y++) {
      let sum = 0; let count = 0;
      for (let x = 0; x < 96; x++) {
        if (pixels[(y * 96 + x) * 4 + 3]! >= 180) { sum += x; count++; }
      }
      if (count) { tips[side] = { x: (sum / count + .5) / 96, y: (y + .5) / 144 }; return; }
    }
  } catch { /* Canvas is optional; the original wand remains fully visible. */ }
}

function stop() {
  cancelAnimationFrame(frame); frame = 0; motion.value = 'still';
  const surface = canvas.value;
  surface?.getContext('2d')?.clearRect(0, 0, surface.width, surface.height);
}
function spellOrigin(side: number, surface: DOMRect) {
  const origin = side === 0 ? playerOrigin.value : opponentOrigin.value;
  const artifact = origin?.querySelector<HTMLElement>('.game-magic-clash__artifact');
  if (!artifact) return { x: surface.width * (side === 0 ? .3 : .7), y: surface.height * .35 };
  const bounds = artifact.getBoundingClientRect();
  const imageTip = (side === 0 ? props.wandUrl : props.opponentWandUrl) && !artworkFailed.value[side] ? tips[side]! : { x: .5, y: .05 };
  const angle = (side === 0 ? 64 : -64) * Math.PI / 180;
  const size = getComputedStyle(artifact);
  const x = (imageTip.x - .5) * Number.parseFloat(size.width);
  const y = (imageTip.y - .5) * Number.parseFloat(size.height);
  return {
    x: bounds.left + bounds.width / 2 - surface.left + x * Math.cos(angle) - y * Math.sin(angle),
    y: bounds.top + bounds.height / 2 - surface.top + x * Math.sin(angle) + y * Math.cos(angle),
  };
}

function play() {
  if (!pendingReveal || !inView || document.hidden || disposed) return;
  pendingReveal = false; stop();
  if (reducedMotion?.matches || !canvas.value || revealedActions.value.length !== 2) return;
  const surface = canvas.value;
  const context = surface.getContext('2d');
  if (!context) return;
  const bounds = surface.getBoundingClientRect();
  const { width, height } = bounds;
  if (!width || !height) return;
  const ratio = Math.min(window.devicePixelRatio || 1, width < 500 ? 1.75 : 2);
  surface.width = Math.round(width * ratio); surface.height = Math.round(height * ratio);
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
  const styles = getComputedStyle(surface);
  const colors = [styles.getPropertyValue('--wns-amber').trim() || '#ffc662', styles.getPropertyValue('--wns-violet').trim() || '#c8b9fc'];
  const actions = [...revealedActions.value];
  const origins = [spellOrigin(0, bounds), spellOrigin(1, bounds)];
  const centerX = width * (props.round?.winner === 'player' ? .56 : props.round?.winner === 'opponent' ? .44 : .5);
  const centerY = height * .36;
  const bloom = context.createRadialGradient(centerX, centerY, 0, centerX, centerY, 68);
  bloom.addColorStop(0, '#fff8e8'); bloom.addColorStop(.15, '#ffe8b7'); bloom.addColorStop(.5, '#d5b5ff66'); bloom.addColorStop(1, '#c8b9fc00');
  let particleCount = width < 500 || navigator.hardwareConcurrency <= 4 ? 56 : 88;
  const started = performance.now();
  let previous = started;
  motion.value = 'playing';
  function draw(now: number) {
    if (disposed || document.hidden || !inView || reducedMotion?.matches) { stop(); return; }
    const elapsed = now - started;
    if (elapsed >= 2000) { stop(); return; }
    if (now - previous > 34) particleCount = Math.min(particleCount, 36);
    previous = now;
    context!.clearRect(0, 0, width, height);
    context!.globalCompositeOperation = 'lighter';
    const arrival = Math.max(0, Math.min(1, (elapsed - 160) / 570));
    const beamOpacity = Math.min(1, elapsed / 200) * Math.max(0, 1 - Math.max(0, elapsed - 950) / 650);
    for (let side = 0; side < 2; side++) {
      const origin = origins[side]!;
      const action = actions[side];
      const spread = action === 'trick' ? 19 : action === 'shield' ? 10 : 4;
      for (let strand = 0; strand < 3; strand++) {
        context!.strokeStyle = strand === 2 ? '#fff9ed' : colors[side];
        context!.lineWidth = strand === 0 ? 15 : strand === 1 ? 4 : 1.25;
        context!.globalAlpha = beamOpacity * (strand === 0 ? .13 : strand === 1 ? .8 : .95);
        context!.beginPath(); context!.moveTo(origin.x, origin.y);
        for (let step = 1; step <= 32; step++) {
          const progress = arrival * step / 32;
          const bend = Math.sin(progress * Math.PI) * Math.sin(progress * (action === 'strike' ? 27 : 12) - elapsed / 100 + side) * spread;
          context!.lineTo(origin.x + (centerX - origin.x) * progress, origin.y + (centerY - origin.y) * progress + bend);
        }
        context!.stroke();
      }
      const impact = Math.max(0, Math.min(1, (elapsed - 700) / 1100));
      if (action === 'shield' && elapsed > 600) {
        context!.strokeStyle = colors[side]; context!.lineWidth = 3;
        context!.globalAlpha = Math.max(0, 1 - impact) * .85;
        context!.beginPath(); context!.arc(centerX, centerY, 18 + impact * 42, side === 0 ? -.9 : Math.PI - .9, side === 0 ? .9 : Math.PI + .9); context!.stroke();
      }
      context!.fillStyle = colors[side];
      for (let index = 0; index < particleCount; index++) {
        const seed = index * 2.39996 + side;
        const travel = Math.max(0, Math.min(1, (elapsed - 160 - index * 2) / 570));
        const burst = Math.max(0, (elapsed - 730 - index * 2) / 1050);
        let x = origin.x + (centerX - origin.x) * travel;
        let y = origin.y + (centerY - origin.y) * travel + Math.sin(seed + travel * 9) * spread * Math.sin(travel * Math.PI);
        if (burst > 0) {
          x = centerX + Math.cos(seed) * burst * width * .4;
          y = centerY + Math.sin(seed) * burst * height * .52 + burst * burst * 35;
        }
        context!.globalAlpha = Math.max(0, (1 - burst) * Math.min(1, travel * 5)) * .95;
        const radius = index % 7 === 0 ? 2.6 : 1.4;
        context!.beginPath(); context!.arc(x, y, radius, 0, Math.PI * 2); context!.fill();
      }
    }
    const impactLight = Math.max(0, 1 - Math.abs(elapsed - 850) / 420);
    context!.globalAlpha = impactLight * .7; context!.fillStyle = bloom;
    context!.fillRect(centerX - 68, centerY - 68, 136, 136);
    context!.globalAlpha = 1; context!.globalCompositeOperation = 'source-over';
    frame = requestAnimationFrame(draw);
  }
  frame = requestAnimationFrame(draw);
}

function visibilityChanged() { if (document.hidden) { pendingReveal = false; stop(); } }
function motionChanged() { if (reducedMotion?.matches) { pendingReveal = false; stop(); } }
// Loaded results and polling the same round never replay the impact.
watch(() => props.round?.index, async (value, previous) => {
  if (value === previous) return;
  stop(); pendingReveal = value !== undefined && !document.hidden;
  await nextTick();
  if (!disposed) play();
});
onMounted(() => {
  reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  reducedMotion.addEventListener('change', motionChanged);
  document.addEventListener('visibilitychange', visibilityChanged);
  if (typeof IntersectionObserver !== 'undefined' && stage.value) {
    observer = new IntersectionObserver(entries => {
      inView = entries.some(entry => entry.isIntersecting);
      if (inView) play(); else stop();
    });
    observer.observe(stage.value);
  } else inView = true;
  if (typeof ResizeObserver !== 'undefined' && stage.value) {
    resizeObserver = new ResizeObserver(stop); resizeObserver.observe(stage.value);
  }
});
onBeforeUnmount(() => {
  disposed = true; stop(); observer?.disconnect(); resizeObserver?.disconnect();
  reducedMotion?.removeEventListener('change', motionChanged);
  document.removeEventListener('visibilitychange', visibilityChanged);
});
</script>

<template>
  <div ref="stage" class="game-clash-stage game-magic-clash" :class="{ 'game-clash-stage--revealed': round, 'game-clash-stage--locked': locked }" :data-motion="motion" role="group" aria-label="Hůlky v souboji">
    <div ref="playerOrigin" class="game-magic-clash__origin">
      <img v-if="wandUrl && !artworkFailed[0]" class="game-magic-clash__artifact" :src="wandUrl" alt="" width="1024" height="1536" @load="artworkLoaded($event, 0)" @error="artworkFailed[0] = true" />
      <svg v-else class="game-magic-clash__artifact game-magic-clash__placeholder" viewBox="0 0 100 150" aria-hidden="true" focusable="false"><path d="m50 7-3 97-4 32q7 9 14 0l-4-32ZM44 116h12m-13 9h14" /></svg>
    </div>
    <div ref="opponentOrigin" class="game-magic-clash__origin game-magic-clash__origin--opponent">
      <img v-if="opponentWandUrl && !artworkFailed[1]" class="game-magic-clash__artifact" :src="opponentWandUrl" alt="" width="1024" height="1536" @load="artworkLoaded($event, 1)" @error="artworkFailed[1] = true" />
      <svg v-else class="game-magic-clash__artifact game-magic-clash__placeholder" viewBox="0 0 100 150" aria-hidden="true" focusable="false"><path d="m50 7-3 97-4 32q7 9 14 0l-4-32ZM44 116h12m-13 9h14" /></svg>
    </div>
    <svg class="game-magic-clash__traces" viewBox="0 0 400 174" preserveAspectRatio="none" aria-hidden="true" focusable="false"><path d="M140 63Q170 52 200 63M260 63Q230 74 200 63" /><circle cx="200" cy="63" r="4" /></svg>
    <canvas ref="canvas" class="game-magic-clash__particles" aria-hidden="true" />
    <div v-if="locked" class="game-magic-clash__waiting" aria-hidden="true">Tah uzamčený<small>Čekáme na soupeře</small></div>
    <div v-else-if="revealedActions.length === 2" class="game-magic-clash__spells" aria-hidden="true"><div v-for="(action, index) in revealedActions" :key="index"><GameSpellToken :action="action" /><span>{{ actionName(action) }}</span></div></div>
    <div class="game-magic-clash__identities"><p class="game-magic-clash__identity">{{ wandName || 'Tvoje hůlka' }}<span v-if="synthetic"> · ukázka</span></p><p class="game-magic-clash__identity">{{ opponentWandName || 'Soupeřova hůlka' }}<span v-if="opponentSynthetic"> · ukázka</span></p></div>
  </div>
</template>

<style scoped>
.game-magic-clash { height: calc(138px + 2.25rem); min-height: calc(138px + 2.25rem); isolation: isolate; overflow: hidden; margin-block: 4px 12px; }
.game-magic-clash__origin { position: absolute; left: 0; top: -4px; width: 44%; height: 140px; display: grid; place-items: center; }
.game-magic-clash__origin--opponent { left: auto; right: 0; }
.game-magic-clash__artifact { display: block; width: clamp(90px, 25vw, 120px); height: auto; max-width: none; transform: rotate(64deg); }
.game-magic-clash__origin--opponent .game-magic-clash__artifact { transform: rotate(-64deg); }
.game-magic-clash__placeholder { fill: var(--wns-surface-raised); stroke: var(--wns-muted); stroke-width: 1.1; }
.game-magic-clash__traces, .game-magic-clash__particles { position: absolute; inset: 0; width: 100%; height: 100%; pointer-events: none; }
.game-magic-clash__traces { fill: none; stroke: var(--wns-muted); stroke-width: 1; opacity: .3; }
.game-magic-clash__spells { position: absolute; inset: auto 33% calc(2.25rem + 4px); display: flex; justify-content: space-between; gap: 12px; }
.game-magic-clash__spells > div { display: grid; justify-items: center; gap: 2px; font-size: .75rem; color: var(--wns-amber); }
.game-magic-clash__spells > div:last-child { color: var(--wns-violet); }
.game-magic-clash__spells .game-spell-token { width: 34px; height: 34px; }
.game-magic-clash__waiting { position: absolute; bottom: calc(2.25rem + 12px); font-size: .9rem; text-align: center; color: var(--wns-text); }
.game-magic-clash__waiting small { display: block; color: var(--wns-muted); margin-top: 4px; }
.game-magic-clash__identities { position: absolute; bottom: 0; inset-inline: 0; display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.game-magic-clash__identity { max-height: 2.25rem; margin: 0; color: var(--wns-muted); font-size: .75rem; line-height: 1.5; text-align: center; overflow-wrap: anywhere; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
.game-magic-clash__identity span { font-size: .7rem; }
@media (min-width: 600px) {
  .game-magic-clash { height: calc(166px + 2.25rem); }
  .game-magic-clash__origin { height: 170px; }
  .game-magic-clash__artifact { width: 136px; }
  .game-magic-clash__spells { inset-inline: 39%; }
}
</style>
