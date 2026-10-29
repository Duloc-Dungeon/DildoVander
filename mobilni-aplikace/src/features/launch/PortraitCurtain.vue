<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import PortraitMirror from '../../components/scene/PortraitMirror.vue';
import placeholder from '../../assets/brand/midnight/placeholder.webp';
import userIcon from '../../assets/wns-v2/icons/regular/user.svg';
import { presentationForeground } from './launchPresentation';

const props = defineProps<{ image: string; identity: string; alt: string; preparing: boolean; unavailable?: boolean }>();
const emit = defineEmits<{ revealed: [value: boolean]; failed: []; replay: [] }>();
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
  <div ref="stage" class="portrait-stage" :class="{ 'is-open': open || unavailable, 'is-preparing': preparing, 'reduced-motion': reduced }" :data-curtain="phase">
    <PortraitMirror :key="identity" :open="open || !!unavailable" :active="presentationForeground" :preparing="preparing && !image && !unavailable" :animate-reveal="phase === 'opening'" :replayable="phase === 'open' && !!image && !unavailable" @replay="emit('replay')">
    <img v-if="image" class="portrait-stage-image" :src="image" :alt="alt" :aria-hidden="phase !== 'open'" fetchpriority="high" @error="emit('failed')" />
    <img v-else-if="unavailable" class="portrait-missing-avatar" :src="userIcon" alt="" aria-hidden="true" />
    <img v-else class="portrait-stage-placeholder" :src="placeholder" alt="" aria-hidden="true" />
    <p v-if="unavailable && !image" class="portrait-unavailable">Portrét zatím chybí.<br />Postava má místo jisté.</p>
    </PortraitMirror>
  </div>
</template>

<style scoped>
.portrait-stage { position: relative; width: 100%; aspect-ratio: 4 / 5; overflow: visible; isolation: isolate; }
.portrait-stage-image, .portrait-stage-placeholder { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; object-position: 50% 30%; }
.portrait-stage-placeholder { object-position: center; }
.portrait-unavailable { position: absolute; inset: auto 9% 7%; margin: 0; padding: 16px; border-radius: 12px; background: var(--wns-violet); color: var(--wns-on-violet); text-align: center; font-size: .875rem; line-height: 1.5; }
.portrait-missing-avatar { position: absolute; width: 40%; height: 40%; inset: 20% 30% auto; }
</style>
