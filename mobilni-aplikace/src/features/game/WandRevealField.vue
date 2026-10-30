<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { useAnimate } from 'motion-v';

withDefaults(defineProps<{ artworkUrl?: string | null; wandName?: string }>(), { artworkUrl: null, wandName: '' });
const anchor = ref<HTMLElement>();
const canvas = ref<HTMLCanvasElement>();
const hero = ref<HTMLElement>();
const [scope, animate] = useAnimate();
const state = ref<'active' | 'settled' | 'disabled'>('active');
const duration = 4.2;
let dispose = () => {};

onMounted(() => {
  const surface = canvas.value;
  const host = anchor.value?.parentElement;
  const root = scope.value;
  const object = hero.value;
  if (!surface || !host || !root || !object) { state.value = 'disabled'; return; }
  let context: CanvasRenderingContext2D | null;
  try { context = surface.getContext('2d'); } catch { context = null; }
  if (!context) { state.value = 'disabled'; return; }
  const paint = context;
  const motion = window.matchMedia('(prefers-reduced-motion: reduce)');
  let frame = 0;
  let previous = 0;
  let slowFrames = 0;
  let exhausted = false;
  let landingMoved = false;
  let playback: { time: number; cancel: () => void } | undefined;
  let timeout: ReturnType<typeof setTimeout> | undefined;
  let pointerX = 0;
  let pointerY = 0;
  let influenceX = 0;
  let influenceY = 0;
  const target = host.getBoundingClientRect();
  const stage = root.getBoundingClientRect();
  const image = object.getBoundingClientRect();
  const width = stage.width;
  const height = stage.height;
  const centerX = image.left + image.width / 2 - stage.left;
  const centerY = image.top + image.height / 2 - stage.top;
  const imageHeight = image.height;
  const ratio = Math.min(window.devicePixelRatio || 1, 1.5);
  surface.width = Math.round(width * ratio);
  surface.height = Math.round(height * ratio);
  paint.setTransform(ratio, 0, 0, ratio, 0, 0);
  const palette = getComputedStyle(surface);
  const mint = palette.getPropertyValue('--wns-mint').trim() || '#b8f3d4';
  const amber = palette.getPropertyValue('--wns-amber').trim() || '#ffc662';
  const ivory = '#f5f5f0';
  const stream = paint.createLinearGradient(0, imageHeight * .42, 0, -imageHeight * .47);
  stream.addColorStop(0, 'rgba(255,198,98,0)');
  stream.addColorStop(.25, amber);
  stream.addColorStop(.7, mint);
  stream.addColorStop(1, 'rgba(184,243,212,0)');
  const dust = Array.from({ length: width < 600 ? 72 : 112 }, (_, index) => ({
    delay: (index % 19) * .075,
    lifetime: .7 + (index % 7) * .09,
    phase: index * 2.399963,
    spread: 18 + (index % 13) * 5,
    size: index % 9 === 0 ? 1.6 : .7,
  }));

  function stop() {
    cancelAnimationFrame(frame);
    frame = 0;
    if (timeout) clearTimeout(timeout);
    timeout = undefined;
    playback?.cancel();
    paint.clearRect(0, 0, width, height);
    state.value = motion.matches || exhausted ? 'disabled' : 'settled';
  }
  function draw(now: number) {
    frame = 0;
    if (motion.matches || document.hidden || exhausted) { stop(); return; }
    if (previous && now - previous > 100) slowFrames++;
    else slowFrames = 0;
    previous = now;
    if (slowFrames >= 10) { exhausted = true; stop(); return; }
    const time = playback?.time ?? 0;
    // Mobile keyboards and browser bars change the viewport during completion.
    // Keep the reveal visible; omit only the flight to a now-stale landing spot.
    if (landingMoved && time >= 2.85) { stop(); return; }
    if (time >= duration) { stop(); return; }
    const energy = Math.max(0, Math.min(1, time / .55, (2.65 - time) / .75));
    influenceX += (pointerX - influenceX) * .08;
    influenceY += (pointerY - influenceY) * .08;
    paint.clearRect(0, 0, width, height);
    paint.save();
    paint.translate(centerX + influenceX * 16, centerY + influenceY * 10);
    // Three rising filaments share the wand's long axis. No rings or radial gauges.
    for (let ribbon = 0; ribbon < 3; ribbon++) {
      const side = ribbon === 1 ? -1 : 1;
      const sweep = Math.sin(time * 2.1 + ribbon * 1.8) * (34 + ribbon * 17) * energy;
      paint.strokeStyle = stream;
      paint.globalAlpha = energy * (ribbon === 0 ? .28 : .16);
      paint.lineWidth = ribbon === 0 ? 10 : 3;
      paint.beginPath();
      paint.moveTo(side * 10, imageHeight * .4);
      paint.bezierCurveTo(sweep - 30, imageHeight * .2,
        -sweep + 28, -imageHeight * .25, side * 2, -imageHeight * .44);
      paint.stroke();
    }
    for (let index = 0; index < dust.length; index++) {
      const particle = dust[index]!;
      const life = (time - particle.delay) / particle.lifetime;
      if (life <= 0 || life >= 1) continue;
      const spread = Math.sin(Math.PI * life) * particle.spread;
      const angle = life * 7 + particle.phase;
      const x = Math.sin(angle) * spread;
      const y = imageHeight * (.42 - .88 * life);
      paint.globalAlpha = Math.sin(Math.PI * life) * energy * .7;
      paint.strokeStyle = index % 5 === 0 ? amber : index % 3 === 0 ? mint : ivory;
      paint.lineWidth = particle.size;
      paint.beginPath();
      paint.moveTo(x, y + 2 + life * 4);
      paint.lineTo(x + Math.cos(angle) * 1.5, y);
      paint.stroke();
    }
    paint.restore();
    frame = requestAnimationFrame(draw);
  }
  function interact(event: PointerEvent) {
    if (state.value !== 'active') return;
    pointerX = Math.max(-1, Math.min(1, event.clientX / Math.max(1, innerWidth) * 2 - 1));
    pointerY = Math.max(-1, Math.min(1, event.clientY / Math.max(1, innerHeight) * 2 - 1));
  }
  function preferenceChanged() {
    if (motion.matches) stop();
    else if (state.value === 'disabled' && !exhausted) state.value = 'settled';
  }
  function visibilityChanged() { if (document.hidden) stop(); }
  function geometryChanged() {
    landingMoved = true;
  }
  const intersection = typeof IntersectionObserver === 'undefined' ? null : new IntersectionObserver(entries => {
    if (!entries.some(entry => entry.isIntersecting)) landingMoved = true;
  });
  intersection?.observe(host);
  window.addEventListener('pointermove', interact, { passive: true });
  window.addEventListener('pointerdown', interact, { passive: true });
  window.addEventListener('resize', geometryChanged, { passive: true });
  window.addEventListener('scroll', geometryChanged, { passive: true });
  document.addEventListener('visibilitychange', visibilityChanged);
  motion.addEventListener('change', preferenceChanged);
  dispose = () => {
    stop();
    intersection?.disconnect();
    window.removeEventListener('pointermove', interact);
    window.removeEventListener('pointerdown', interact);
    window.removeEventListener('resize', geometryChanged);
    window.removeEventListener('scroll', geometryChanged);
    document.removeEventListener('visibilitychange', visibilityChanged);
    motion.removeEventListener('change', preferenceChanged);
  };
  if (motion.matches || document.hidden || width <= 0 || height <= 0) { stop(); return; }
  if (target.bottom <= 0 || target.top >= innerHeight || target.right <= 0 || target.left >= innerWidth) landingMoved = true;
  const landingX = target.left + target.width / 2 - (image.left + image.width / 2);
  const landingY = target.top + target.height / 2 - (image.top + image.height / 2);
  const landingScale = Math.min(target.width / Math.max(1, image.width), target.height / Math.max(1, image.height));
  try {
    const controller = animate([
      ['.wand-reveal-object', { opacity: [0, 1], y: [24, 0], scale: [.91, 1], rotate: [-2, 0] },
        { at: .16, duration: .85, ease: [.16, 1, .3, 1] }],
      ['.wand-reveal-key', { opacity: [0, .85, 0], scaleX: [.5, 1.2, 1] },
        { at: .08, duration: 2.55, times: [0, .45, 1], ease: 'easeInOut' }],
      ['.wand-reveal-flare', { opacity: [0, .45, 0], scale: [.4, 1.4, 2.8] },
        { at: 1.05, duration: .75, times: [0, .25, 1] }],
      ['.wand-reveal-name', { opacity: [0, 1], y: [8, 0] },
        { at: .95, duration: .65, ease: [.16, 1, .3, 1] }],
      ['.wand-reveal-name', { opacity: [1, 0] }, { at: 2.5, duration: .3 }],
      ['.wand-reveal-backdrop', { opacity: [1, 0] }, { at: 2.85, duration: .95, ease: 'easeInOut' }],
      ['.wand-reveal-object', { x: [0, landingX], y: [0, landingY], scale: [1, landingScale] },
        { at: 2.85, duration: 1.35, ease: [.22, 1, .36, 1] }],
    ]);
    playback = controller;
    controller.then(() => { if (state.value === 'active') stop(); });
    timeout = setTimeout(stop, (duration + .25) * 1000);
    draw(performance.now());
  } catch {
    exhausted = true;
    stop();
  }
});
onBeforeUnmount(() => dispose());
</script>

<template>
  <span ref="anchor" class="wand-reveal-anchor" :data-state="state" aria-hidden="true">
    <Teleport to="body">
      <div v-show="state === 'active'" ref="scope" class="wand-reveal-viewport" :data-state="state" aria-hidden="true">
        <div class="wand-reveal-backdrop"></div>
        <div class="wand-reveal-key"></div>
        <canvas ref="canvas" class="wand-reveal-field" :data-state="state" aria-hidden="true"></canvas>
        <div class="wand-reveal-object-plane">
          <div ref="hero" class="wand-reveal-object">
            <img v-if="artworkUrl" :src="artworkUrl" alt="" decoding="sync" draggable="false" referrerpolicy="no-referrer" />
          </div>
        </div>
        <div class="wand-reveal-flare"></div>
        <p class="wand-reveal-name">{{ wandName }}</p>
      </div>
    </Teleport>
  </span>
</template>

<style scoped>
.wand-reveal-anchor { display: contents; }
.wand-reveal-viewport {
  position: fixed;
  inset: -2px;
  z-index: 190;
  overflow: hidden;
  pointer-events: none;
  isolation: isolate;
  font-family: var(--wns-display, Manrope, sans-serif);
  color: #f5f5f0;
  /* The viewport shell never translates, rotates, scales or fades. */
  transform: none;
  animation: none;
}
.wand-reveal-backdrop { position: absolute; inset: 0; background: #080909; opacity: 1; }
.wand-reveal-field { position: absolute; inset: 0; width: 100%; height: 100%; pointer-events: none; }
.wand-reveal-object-plane { position: absolute; inset: 0 0 10dvh; display: grid; place-items: center; }
.wand-reveal-object { height: min(76dvh, 64rem); aspect-ratio: 2 / 3; justify-self: center; opacity: 0; transform-origin: center; }
.wand-reveal-object img { display: block; width: 100%; height: 100%; object-fit: contain; }
.wand-reveal-key {
  position: absolute;
  left: calc(50% - 3rem);
  top: 4%;
  bottom: 18%;
  width: 6rem;
  background: linear-gradient(0deg, transparent, #ffc66226 25%, #b8f3d45c 58%, transparent);
  filter: blur(28px);
  opacity: 0;
}
.wand-reveal-flare {
  position: absolute;
  top: 8%;
  left: calc(50% - 6rem);
  width: 12rem;
  height: 12rem;
  border-radius: 50%;
  background: radial-gradient(ellipse, #f5f5f0cc, #b8f3d420 35%, transparent 70%);
  opacity: 0;
}
.wand-reveal-name {
  position: absolute;
  left: 1.5rem;
  right: 1.5rem;
  bottom: max(2rem, 6dvh);
  margin: 0;
  text-align: center;
  font-size: clamp(1.6rem, 4vw, 3rem);
  font-weight: 650;
  line-height: 1.1;
  letter-spacing: -.025em;
  text-wrap: balance;
  opacity: 0;
}
@media (prefers-reduced-motion: reduce) { .wand-reveal-viewport { display: none !important; } }
</style>
