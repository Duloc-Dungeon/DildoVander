<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import closed from '../../assets/brand/curtain-closed-concept.png';
import frame from '../../assets/brand/curtain-open-concept.png';
import { presentationForeground } from './launchPresentation';

const props = defineProps<{ image: string; identity: string; alt: string; preparing: boolean }>();
const emit = defineEmits<{ revealed: [value: boolean]; failed: [] }>();
const stage = ref<HTMLElement>();
const phase = ref<'closed' | 'opening' | 'open'>('closed');
const visible = ref(false);
const reduced = ref(false);
const storagePrefix = 'wand.portrait-seen.v1.';
let observer: IntersectionObserver | undefined;
let media: MediaQueryList | undefined;
let completion: ReturnType<typeof setTimeout> | undefined;
let generation = 0;
const open = computed(() => phase.value !== 'closed');
function seen(): boolean {
  try { return !!props.identity && localStorage.getItem(storagePrefix + props.identity) === 'seen'; } catch { return false; }
}
function cancel(): void {
  generation++;
  if (completion) clearTimeout(completion);
  completion = undefined;
  if (!seen()) { phase.value = 'closed'; emit('revealed', false); }
}
function finish(expected: number): void {
  if (expected !== generation || !visible.value || !presentationForeground.value || !props.image || !props.identity) return;
  phase.value = 'open';
  try { localStorage.setItem(storagePrefix + props.identity, 'seen'); } catch { /* Replaying after a restart is safe. */ }
  emit('revealed', true);
}
function evaluate(): void {
  if (!props.image || !props.identity) { cancel(); phase.value = 'closed'; emit('revealed', false); return; }
  if (seen()) { phase.value = 'open'; emit('revealed', true); return; }
  if (!visible.value || !presentationForeground.value) { cancel(); return; }
  if (phase.value !== 'closed') return;
  const expected = ++generation;
  phase.value = reduced.value ? 'open' : 'opening';
  if (reduced.value) {
    // Two paints establish an actual static presentation; no animationend dependency.
    requestAnimationFrame(() => requestAnimationFrame(() => finish(expected)));
  } else completion = setTimeout(() => finish(expected), 1_100);
}
function motion(event: MediaQueryListEvent): void { reduced.value = event.matches; cancel(); evaluate(); }
function leaving(): void { visible.value = false; cancel(); }
watch(() => [props.identity, props.image], async () => { cancel(); phase.value = 'closed'; emit('revealed', false); await nextTick(); evaluate(); }, { flush: 'sync' });
watch(presentationForeground, evaluate);
onMounted(() => {
  media = window.matchMedia('(prefers-reduced-motion: reduce)');
  reduced.value = media.matches;
  media.addEventListener('change', motion);
  observer = new IntersectionObserver(entries => {
    visible.value = !!entries[0]?.isIntersecting && entries[0].intersectionRatio >= .5;
    evaluate();
  }, { threshold: [0, .5, 1] });
  if (stage.value) observer.observe(stage.value);
  window.addEventListener('pagehide', leaving);
  evaluate();
});
onBeforeUnmount(() => { cancel(); observer?.disconnect(); media?.removeEventListener('change', motion); window.removeEventListener('pagehide', leaving); });
</script>

<template>
  <div ref="stage" class="portrait-stage" :class="{ 'is-open': open, 'is-preparing': preparing, 'reduced-motion': reduced }" :data-curtain="phase">
    <img v-if="image" class="portrait-stage-image" :src="image" :alt="alt" :aria-hidden="phase !== 'open'" @error="emit('failed')" />
    <div class="curtain-wings" aria-hidden="true">
      <img class="curtain-wing curtain-wing-left" :src="closed" alt="" width="1122" height="1402" />
      <img class="curtain-wing curtain-wing-right" :src="closed" alt="" width="1122" height="1402" />
    </div>
    <img class="curtain-floor" :src="closed" alt="" aria-hidden="true" width="1122" height="1402" />
    <img class="curtain-frame" :src="frame" alt="" aria-hidden="true" width="1122" height="1402" />
  </div>
</template>

<style scoped>
.portrait-stage { position: relative; width: 100%; aspect-ratio: 4 / 5; overflow: hidden; isolation: isolate; background: var(--aw-void); border-radius: 8px; }
.portrait-stage-image { position: absolute; inset: 2% 3%; width: 94%; height: 96%; object-fit: cover; object-position: 50% 30%; }
.curtain-wings { position: absolute; inset: 0; overflow: hidden; }
.curtain-wing, .curtain-frame, .curtain-floor { position: absolute; inset: 0; width: 100%; height: 100%; }
.curtain-floor { clip-path: inset(96% 0 0); pointer-events: none; }
.curtain-wing { transition: none; will-change: transform; }
.curtain-wing-left { clip-path: inset(8% 49.8% 3% 2%); }
.curtain-wing-right { clip-path: inset(8% 2% 3% 49.8%); }
.is-open .curtain-wing { transition: transform 1.1s cubic-bezier(.32, 0, .2, 1); }
.is-open .curtain-wing-left { transform: translateX(-43%); }
.is-open .curtain-wing-right { transform: translateX(43%); }
.curtain-frame { pointer-events: none; }
.is-preparing:not(.is-open) .curtain-wings { filter: brightness(1.08); }
.reduced-motion .curtain-wing { transition: none; }
@media (prefers-reduced-motion: reduce) { .curtain-wing { transition: none; } }
</style>
