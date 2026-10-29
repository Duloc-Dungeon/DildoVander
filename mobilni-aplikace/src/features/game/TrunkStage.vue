<script setup lang="ts">
import { computed, ref, watch, type CSSProperties } from 'vue';
import type { TrunkStageProps } from './TrunkStage.types';

/** Presentation only. Progress and sound levels come from the workshop adapter. */
const props = withDefaults(defineProps<TrunkStageProps>(), {
  assistantOutputLevel: 0,
  artworkStatus: 'not-requested',
  wandName: '',
  wandArtworkUrl: null,
  showCaption: true,
  showArtworkStatus: true,
  trunkWidth: 960,
  trunkHeight: 960,
  artworkPlacement: 'standalone',
  trayArtworkWidthPercent: 76,
});
const emit = defineEmits<{
  'artwork-loaded': [];
  'artwork-error': [reason: 'decode'];
}>();

const trunk640 = new URL('../../assets/wns-v2/art/trunk-workshop/trunk-open-640.webp', import.meta.url).href;
const trunk960 = new URL('../../assets/wns-v2/art/trunk-workshop/trunk-open-960.webp', import.meta.url).href;
const closed640 = new URL('../../assets/wns-v2/art/trunk-workshop/trunk-closed-640.webp', import.meta.url).href;
const closed960 = new URL('../../assets/wns-v2/art/trunk-workshop/trunk-closed-960.webp', import.meta.url).href;
const closed = computed(() => ['ready', 'unavailable'].includes(props.stage));
const source = computed(() => props.trunkSrc || (closed.value ? closed960 : trunk960));
const sources = computed(() => props.trunkSrcSet
  || (props.trunkSrc ? undefined : closed.value ? `${closed640} 640w, ${closed960} 960w` : `${trunk640} 640w, ${trunk960} 960w`));

const level = computed(() => props.stage === 'speaking'
  && Number.isFinite(props.assistantOutputLevel)
  ? Math.min(1, Math.max(0, props.assistantOutputLevel)) : 0);
const visualStyle = computed<CSSProperties>(() => ({
  '--trunk-lift': `${-2 * level.value}px`,
  '--trunk-scale': String(1 + 0.006 * level.value),
  '--trunk-emission': String(0.22 * level.value),
  aspectRatio: `${Math.max(1, props.trunkWidth)} / ${Math.max(1, props.trunkHeight)}`,
}));

/** Only local authenticated delivery. For a Capacitor remote API, the parent
 * fetches via its authorized adapter and supplies an owned blob URL instead.
 * The parent must revoke its blob on replacement/unmount. This guard is not
 * authorization; the backend still enforces ownership on every asset request.
 */
const artworkSource = computed(() => {
  const value = props.wandArtworkUrl?.trim();
  if (!value || typeof window === 'undefined') return null;
  try {
    const candidate = new URL(value, window.location.href);
    const currentOrigin = window.location.origin;
    // Native webviews can use an opaque blob origin. The adapter owns and
    // authorizes the blob; the browser enforces its document access scope.
    if (candidate.protocol === 'blob:') return candidate.href;
    if (candidate.origin === currentOrigin
      && (candidate.protocol === 'https:' || candidate.protocol === 'http:')
      && !candidate.username && !candidate.password) return candidate.href;
  } catch { /* Show the normal unavailable-artwork state. */ }
  return null;
});

const decoded = ref(false);
const decodeError = ref(false);
watch(() => [artworkSource.value, props.artworkStatus] as const, () => {
  decoded.value = false;
  decodeError.value = false;
});
const revealed = computed(() => props.stage === 'revealed');
const showTray = computed(() => props.artworkPlacement === 'tray-horizontal'
  && Boolean(props.revealTraySrc));
const artworkStyle = computed<CSSProperties>(() => ({
  '--trunk-tray-artwork-width': `${Number.isFinite(props.trayArtworkWidthPercent)
    ? Math.min(84, Math.max(40, props.trayArtworkWidthPercent)) : 76}%`,
}));
const showArtwork = computed(() => props.artworkStatus === 'ready'
  && artworkSource.value !== null && !decodeError.value);
const artworkMessage = computed(() => {
  if (decodeError.value || props.artworkStatus === 'failed'
    || props.artworkStatus === 'unknown'
    || (props.artworkStatus === 'ready' && !artworkSource.value)) {
    return 'Ilustrace není dostupná. Tvoje hůlka zůstává uložená.';
  }
  if (props.artworkStatus === 'queued' || props.artworkStatus === 'running') {
    return 'Hůlka je uložená. Její ilustrace se připravuje.';
  }
  if (props.artworkStatus === 'ready' && !decoded.value) {
    return 'Načítáme ilustraci uložené hůlky.';
  }
  return 'Tato hůlka zatím nemá uloženou ilustraci.';
});
function onArtworkLoad(event: Event) {
  if ((event.target as HTMLImageElement).getAttribute('src') !== artworkSource.value) return;
  decoded.value = true;
  emit('artwork-loaded');
}
function onArtworkError(event: Event) {
  if ((event.target as HTMLImageElement).getAttribute('src') !== artworkSource.value) return;
  decodeError.value = true;
  decoded.value = false;
  emit('artwork-error', 'decode');
}
</script>

<template>
  <div class="trunk-stage" :data-stage="stage">
    <div v-if="!revealed" class="trunk-stage__visual" :style="visualStyle" aria-hidden="true">
      <span class="trunk-stage__emission"></span>
      <img
        class="trunk-stage__trunk"
        :src="source"
        :srcset="sources"
        sizes="(min-width: 900px) 480px, (min-width: 520px) 440px, calc(100vw - 48px)"
        :width="trunkWidth"
        :height="trunkHeight"
        alt=""
        decoding="async"
        draggable="false"
      />
    </div>

    <figure v-else class="trunk-stage__result">
      <div class="trunk-stage__artwork" :data-loaded="decoded" :data-with-tray="showTray" :style="artworkStyle">
        <img
          v-if="showTray && revealTraySrc"
          class="trunk-stage__tray"
          :src="revealTraySrc"
          alt=""
          decoding="async"
          draggable="false"
          aria-hidden="true"
        />
        <img
          v-if="showArtwork && artworkSource"
          :key="artworkSource"
          class="trunk-stage__wand"
          :src="artworkSource"
          :alt="wandName ? `Digitální ilustrace hůlky ${wandName}` : 'Digitální ilustrace tvé hůlky'"
          decoding="async"
          referrerpolicy="no-referrer"
          @load="onArtworkLoad"
          @error="onArtworkError"
        />
        <p v-if="showArtworkStatus && (!showArtwork || !decoded)" class="trunk-stage__artwork-state" role="status">
          {{ artworkMessage }}
        </p>
      </div>
      <figcaption v-if="showCaption && wandName" class="trunk-stage__name">{{ wandName }}</figcaption>
    </figure>
  </div>
</template>

<style scoped>
.trunk-stage {
  min-width: 0;
  color: var(--aw-ivory, #f5f5f0);
  font-family: inherit;
  background: transparent;
}
.trunk-stage__visual {
  position: relative;
  isolation: isolate;
  width: min(100%, 32rem);
  margin-inline: auto;
}
.trunk-stage__trunk {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: contain;
  transform: translateY(var(--trunk-lift)) scale(var(--trunk-scale));
  transform-origin: 50% 82%;
  transition: transform 100ms linear;
  user-select: none;
}
.trunk-stage__emission {
  position: absolute;
  z-index: -1;
  inset: 24% 18% 22%;
  border-radius: 50%;
  background: radial-gradient(ellipse, var(--wns-mint, #b8f3d4), transparent 68%);
  opacity: var(--trunk-emission);
  transition: opacity 100ms linear;
  pointer-events: none;
}
.trunk-stage__result { margin: 0; }
.trunk-stage__artwork {
  position: relative;
  display: grid;
  place-items: center;
  width: min(100%, 32rem);
  min-height: 16rem;
  aspect-ratio: 1;
  margin-inline: auto;
  isolation: isolate;
}
.trunk-stage__tray {
  position: absolute;
  z-index: -1;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: contain;
  pointer-events: none;
}
.trunk-stage__wand {
  display: block;
  grid-area: 1 / 1;
  width: 100%;
  height: 100%;
  max-height: 32rem;
  object-fit: contain;
  opacity: 0;
}
.trunk-stage__artwork[data-loaded='true'] .trunk-stage__wand { opacity: 1; }
.trunk-stage__artwork[data-with-tray='true'] .trunk-stage__wand {
  width: var(--trunk-tray-artwork-width);
  justify-self: center;
}
.trunk-stage__artwork-state {
  grid-area: 1 / 1;
  max-width: 28ch;
  padding: 1rem;
  margin: 0;
  color: var(--aw-muted, #b2b9b6);
  background: var(--aw-void, #080909);
  border-radius: var(--aw-radius-control, 14px);
  font-size: 1rem;
  line-height: 1.5;
  text-align: center;
}
.trunk-stage__name {
  margin-top: 1rem;
  font-size: clamp(1.25rem, 5vw, 1.75rem);
  font-weight: 760;
  line-height: 1.2;
  text-align: center;
  overflow-wrap: anywhere;
}
@media (max-width: 360px) {
  .trunk-stage__visual { max-width: 18rem; }
  .trunk-stage__artwork { min-height: 12rem; }
}
@media (prefers-reduced-motion: reduce) {
  .trunk-stage__trunk { transform: none; transition: none; }
  .trunk-stage__emission { opacity: 0; transition: none; }
}
</style>
